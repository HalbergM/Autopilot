using System;
using System.Collections.Generic;
using Rynchodon.Utility;
using Sandbox.ModAPI;
using VRage.Collections;

namespace Rynchodon.AntennaRelay
{
	/// <summary>
	/// Holds transmissions for one or more connected Node
	/// </summary>
	public class NetworkStorage
	{

		private const ulong s_cleanInterval = Globals.UpdatesPerSecond * 60;

		/// <summary>
		/// Send a LastSeen to one or more NetworkStorage. Faster than looping through the collection and invoking Receive() for each one.
		/// </summary>
		public static void Receive(ICollection<NetworkStorage> storage, LastSeen seen)
		{
			HashSet<NetworkStorage> sendToSet = ResourcePool<HashSet<NetworkStorage>>.Pool.Get();
			try
			{
				foreach (NetworkStorage sto in storage)
					AddStorage(sendToSet, sto);

				foreach (NetworkStorage sto in sendToSet)
					using (sto.lock_lastSeen.AcquireExclusiveUsing())
						sto.in_Receive(seen);
			}
			finally
			{
				sendToSet.Clear();
				ResourcePool<HashSet<NetworkStorage>>.Pool.Return(sendToSet);
			}
		}

		/// <summary>
		/// <para>Adds a NetworkStorage to s_sendToSet and, if it is not already present, all NetworkStorage it will push to.</para>
		///// <para>lock_sendToSet should be exclusively locked before invoking this method.</para>
		/// </summary>
		/// <param name="storage">NetworkStorage to add to sendToSet</param>
		private static void AddStorage(HashSet<NetworkStorage> sendToSet, NetworkStorage storage)
		{
			if (sendToSet.Add(storage))
				using (storage.lock_pushTo_count.AcquireSharedUsing())
					foreach (NetworkNode connected in storage.m_pushTo_count.Keys)
						if (connected.Storage != null)
							AddStorage(sendToSet, connected.Storage);
		}

		private readonly Logger m_logger;

		private readonly FastResourceLock lock_lastSeen = new FastResourceLock();
		private readonly Dictionary<long, LastSeen> m_lastSeen = new Dictionary<long, LastSeen>();
		private readonly FastResourceLock lock_messages = new FastResourceLock();
		private readonly HashSet<Message> m_messages = new HashSet<Message>();
		private readonly FastResourceLock lock_pushTo_count = new FastResourceLock();
		/// <summary>For one way radio communication.</summary>
		private readonly Dictionary<NetworkNode, int> m_pushTo_count = new Dictionary<NetworkNode, int>();
		private readonly FastResourceLock lock_messageHandlers = new FastResourceLock();
		private readonly Dictionary<long, Action<Message>> m_messageHandlers = new Dictionary<long, Action<Message>>();

		public readonly NetworkNode PrimaryNode;

		private ulong m_nextClean_lastSeen, m_nextClean_message;

		/// <summary>Total number of transmissions stored.</summary>
		public int Size { get { return m_lastSeen.Count + m_messages.Count; } }

		/// <summary>Number of LastSeen stored</summary>
		public int LastSeenCount { get { return m_lastSeen.Count; } }

		public NetworkStorage(NetworkNode primary)
		{
			this.m_logger = new Logger(GetType().Name, () => primary.LoggingName);
			this.PrimaryNode = primary;

			m_logger.debugLog("Created", "NetworkStorage()", Logger.severity.DEBUG);
		}

		/// <summary>
		/// Creates a new NetworkStorage with all the same LastSeen and Message. Used when nodes lose connection.
		/// </summary>
		/// <param name="primary">The node that will be the primary for the new storage.</param>
		/// <returns>A new storage that is a copy of this one.</returns>
		public NetworkStorage Clone(NetworkNode primary)
		{
			m_logger.debugLog("cloning, primary " + primary.LoggingName, "Clone()", Logger.severity.DEBUG);

			NetworkStorage clone = new NetworkStorage(primary);

			// locks need not be held on clone
			ForEachLastSeen(clone.m_lastSeen.Add);
			ForEachMessage(msg => clone.m_messages.Add(msg));

			return clone;
		}

		/// <summary>
		/// Copies all the transmissions to the target storage. Used when merging storages.
		/// </summary>
		/// <param name="recipient">NetworkStorage to copy transmissions to.</param>
		public void CopyTo(NetworkStorage recipient)
		{
			m_logger.debugLog("copying to, " + recipient.PrimaryNode.LoggingName, "CopyTo()", Logger.severity.DEBUG);

			using (recipient.lock_lastSeen.AcquireExclusiveUsing())
				ForEachLastSeen(recipient.in_Receive);
			using (recipient.lock_messages.AcquireExclusiveUsing())
				ForEachMessage(recipient.in_Receive);
		}

		/// <summary>
		/// Add a network connection that data will be pushed through.
		/// </summary>
		/// <param name="node">Node that will receive data.</param>
		public void AddPushTo(NetworkNode node)
		{
			int count;
			using (lock_pushTo_count.AcquireExclusiveUsing())
			{
				if (!m_pushTo_count.TryGetValue(node, out count))
					count = 0;
				m_pushTo_count[node] = count + 1;
			}

			m_logger.debugLog("added push to: " + node.LoggingName + ", count: " + (count + 1), "AddPushTo()", Logger.severity.DEBUG);

			if (count != 0)
				return;

			foreach (NetworkNode n in m_pushTo_count.Keys)
				if (node.Storage == n.Storage)
					return;

			CopyTo(node.Storage);
		}

		/// <summary>
		/// Remove a network connection that data was pushed through.
		/// </summary>
		/// <param name="node">Node that was receiving data.</param>
		public void RemovePushTo(NetworkNode node)
		{
			using (lock_pushTo_count.AcquireExclusiveUsing())
			{
				int count = m_pushTo_count[node];
				if (count == 1)
					m_pushTo_count.Remove(node);
				else
					m_pushTo_count[node] = count - 1;

				m_logger.debugLog("removed push to: " + node.LoggingName + ", count: " + (count - 1), "AddPushTo()", Logger.severity.DEBUG);
			}
		}

		/// <summary>
		/// Register a handler for messages for a block. The handler may receive messages before this method returns.
		/// </summary>
		/// <param name="entityId">The EntityId of the block to register for</param>
		/// <param name="handler">Action to invoke on messages</param>
		/// <exception cref="ArgumentException">iff the block already has a handler registered.</exception>
		public void AddMessageHandler(long entityId, Action<Message> handler)
		{
			using (lock_messageHandlers.AcquireExclusiveUsing())
				m_messageHandlers.Add(entityId, handler);

			ForEachMessage(message => {
				if (message.DestCubeBlock.EntityId == entityId)
				{
					message.IsValid = false;
					handler(message);
				}
			});
		}

		/// <summary>
		/// Unregister a client as handling messages for a block. Does nothing if no handler is registerd for client's block
		/// </summary>
		/// <param name="entityId">The EntityId of the block to unregister for</param>
		public void RemoveMessageHandler(long entityId)
		{
			using (lock_messageHandlers.AcquireExclusiveUsing())
				m_messageHandlers.Remove(entityId);
		}

		/// <summary>
		/// <para>Add a LastSeen to this storage or updates an existing one. LastSeen will be pushed to connected storages.</para>
		/// <para>Not optimized for use within a loop.</para>
		/// </summary>
		/// <param name="seen">LastSeen data to receive.</param>
		public void Receive(LastSeen seen)
		{
			HashSet<NetworkStorage> sendToSet = ResourcePool<HashSet<NetworkStorage>>.Pool.Get();
			try
			{
				AddStorage(sendToSet, this);

				foreach (NetworkStorage sto in sendToSet)
				{
					using (sto.lock_lastSeen.AcquireExclusiveUsing())
						sto.in_Receive(seen);
					if (sto.m_nextClean_lastSeen <= Globals.UpdateCount)
					{
						m_logger.debugLog("Running cleanup on last seen", "Receive()", Logger.severity.INFO);
						sto.ForEachLastSeen(s => { });
					}
				}
			}
			finally
			{
				sendToSet.Clear();
				ResourcePool<HashSet<NetworkStorage>>.Pool.Return(sendToSet);
			}
		}

		/// <summary>
		/// <para>Add a Message to this storage. Message will be pushed to connected storages.</para>
		/// <para>Not optimized for use within a loop.</para>
		/// </summary>
		/// <param name="msg">Message to receive.</param>
		public void Receive(Message msg)
		{
			HashSet<NetworkStorage> sendToSet = ResourcePool<HashSet<NetworkStorage>>.Pool.Get();
			try
			{
				AddStorage(sendToSet, this);

				foreach (NetworkStorage sto in sendToSet)
				{
					using (sto.lock_messages.AcquireExclusiveUsing())
						sto.in_Receive(msg);
					if (sto.m_nextClean_message <= Globals.UpdateCount)
					{
						m_logger.debugLog("Running cleanup on message", "Receive()", Logger.severity.INFO);
						sto.ForEachMessage(s => { });
					}
				}
			}
			finally
			{
				sendToSet.Clear();
				ResourcePool<HashSet<NetworkStorage>>.Pool.Return(sendToSet);
			}
		}

		/// <summary>
		/// <para>For each LastSeen add to this storage or updates an existing LastSeen. LastSeen will be pushed to connected storages.</para>
		/// </summary>
		/// <param name="seen">Collection of LastSeen entitites.</param>
		public void Receive(ICollection<LastSeen> seen)
		{
			HashSet<NetworkStorage> sendToSet = ResourcePool<HashSet<NetworkStorage>>.Pool.Get();
			try
			{
				AddStorage(sendToSet, this);

				foreach (NetworkStorage sto in sendToSet)
				{
					using (sto.lock_lastSeen.AcquireExclusiveUsing())
						foreach (LastSeen s in seen)
							sto.in_Receive(s);
					if (sto.m_nextClean_lastSeen <= Globals.UpdateCount)
					{
						m_logger.debugLog("Running cleanup on last seen", "Receive()", Logger.severity.INFO);
						sto.ForEachLastSeen(s => { });
					}
				}
			}
			finally
			{
				sendToSet.Clear();
				ResourcePool<HashSet<NetworkStorage>>.Pool.Return(sendToSet);
			}
		}

		/// <summary>
		/// Perform an action on each LastSeen. Invalid LastSeen are removed by this method.
		/// </summary>
		/// <param name="method">Action invoked on each LastSeen.</param>
		public void ForEachLastSeen(Action<LastSeen> method)
		{
			List<LastSeen> invalid = null;

			using (lock_lastSeen.AcquireSharedUsing())
				foreach (LastSeen seen in m_lastSeen.Values)
				{
					if (seen.IsValid)
						method(seen);
					else
					{
						if (invalid == null)
							invalid = new List<LastSeen>();
						invalid.Add(seen);
					}
				}

			if (invalid != null)
			{
				m_logger.debugLog("Removing " + invalid.Count + " invalid LastSeen", "ForEachLastSeen()", Logger.severity.DEBUG);
				using (lock_lastSeen.AcquireExclusiveUsing())
					foreach (LastSeen seen in invalid)
						m_lastSeen.Remove(seen.Entity.EntityId);
			}

			m_nextClean_lastSeen = Globals.UpdateCount + s_cleanInterval;
		}

		/// <summary>
		/// Perform an action on each LastSeen. Invalid LastSeen are removed by this method.
		/// </summary>
		/// <param name="method">Action invoked on each EntityId, LastSeen pair.</param>
		public void ForEachLastSeen(Action<long, LastSeen> method)
		{
			List<long> invalid = null;

			using (lock_lastSeen.AcquireSharedUsing())
				foreach (var pair in m_lastSeen)
				{
					if (pair.Value.IsValid)
						method(pair.Key, pair.Value);
					else
					{
						if (invalid == null)
							invalid = new List<long>();
						invalid.Add(pair.Key);
					}
				}

			if (invalid != null)
			{
				m_logger.debugLog("Removing " + invalid.Count + " invalid LastSeen", "ForEachLastSeen()", Logger.severity.DEBUG);
				using (lock_lastSeen.AcquireExclusiveUsing())
					foreach (long id in invalid)
						m_lastSeen.Remove(id);
			}

			m_nextClean_lastSeen = Globals.UpdateCount + s_cleanInterval;
		}

		/// <summary>
		/// Perform an action on each LastSeen. Invalid LastSeen are removed by this method.
		/// </summary>
		/// <param name="method">Function invoked on each LastSeen. If it returns true, short-curcuit.</param>
		public void SearchLastSeen(Func<LastSeen, bool> method)
		{
			List<LastSeen> invalid = null;
			bool found = false;

			using (lock_lastSeen.AcquireSharedUsing())
				foreach (LastSeen seen in m_lastSeen.Values)
				{
					if (seen.IsValid)
					{
						if (method(seen))
						{
							found = true;
							break;
						}
					}
					else
					{
						if (invalid == null)
							invalid = new List<LastSeen>();
						invalid.Add(seen);
					}
				}

			if (invalid != null)
			{
				m_logger.debugLog("Removing " + invalid.Count + " invalid LastSeen", "SearchLastSeen()", Logger.severity.DEBUG);
				using (lock_lastSeen.AcquireExclusiveUsing())
					foreach (LastSeen seen in invalid)
						m_lastSeen.Remove(seen.Entity.EntityId);
			}

			if (!found)
				m_nextClean_lastSeen = Globals.UpdateCount + s_cleanInterval;
		}

		/// <summary>
		/// Perform an action on each LastSeen. Invalid LastSeen are removed by this method.
		/// </summary>
		/// <param name="method">Function invoked on each EntityId, LastSeen pair. If it returns true, short-curcuit.</param>
		public void SearchLastSeen(Func<long, LastSeen, bool> method)
		{
			List<long> invalid = null;
			bool found = false;

			using (lock_lastSeen.AcquireSharedUsing())
				foreach (var pair in m_lastSeen)
				{
					if (pair.Value.IsValid)
					{
						if (method(pair.Key, pair.Value))
						{
							found = true;
							break;
						}
					}
					else
					{
						if (invalid == null)
							invalid = new List<long>();
						invalid.Add(pair.Key);
					}
				}

			if (invalid != null)
			{
				m_logger.debugLog("Removing " + invalid.Count + " invalid LastSeen", "SearchLastSeen()", Logger.severity.DEBUG);
				using (lock_lastSeen.AcquireExclusiveUsing())
					foreach (long id in invalid)
						m_lastSeen.Remove(id);
			}

			if (!found)
				m_nextClean_lastSeen = Globals.UpdateCount + s_cleanInterval;
		}

		/// <summary>
		/// Try to get a LastSeen by EntitiyId.
		/// </summary>
		public bool TryGetLastSeen(long entityId, out LastSeen seen)
		{
			using (lock_lastSeen.AcquireSharedUsing())
				return m_lastSeen.TryGetValue(entityId, out seen);
		}

		/// <summary>
		/// Perform an action on each Message. Invalid Message are removed by this method.
		/// </summary>
		/// <param name="method">Action to invoke on each message.</param>
		private void ForEachMessage(Action<Message> method)
		{
			List<Message> invalid = null;

			using (lock_messages.AcquireSharedUsing())
				foreach (Message msg in m_messages)
				{
					if (msg.IsValid)
						method(msg);
					else
					{
						if (invalid == null)
							invalid = new List<Message>();
						invalid.Add(msg);
					}
				}

			if (invalid != null)
			{
				m_logger.debugLog("Removing " + invalid.Count + " invalid Message", "ForEachMessage()", Logger.severity.DEBUG);
				using (lock_messages.AcquireExclusiveUsing())
					foreach (Message msg in invalid)
						m_messages.Remove(msg);
			}

			m_nextClean_message = Globals.UpdateCount;
		}

		/// <summary>
		/// <para>Internal receive method. Adds the LastSeen to this storage or updates an existing one.</para>
		/// <para>lock_m_lastSeen should be exclusively locked before invoking this method.</para>
		/// </summary>
		private void in_Receive(LastSeen seen)
		{
			LastSeen toUpdate;
			if (m_lastSeen.TryGetValue(seen.Entity.EntityId, out toUpdate))
			{
				if (seen.update(ref toUpdate))
					m_lastSeen[toUpdate.Entity.EntityId] = toUpdate;
			}
			else
				m_lastSeen.Add(seen.Entity.EntityId, seen);
		}

		/// <summary>
		/// <para>Internal receive method. Adds the Message to this storage.</para>
		/// <para>lock_m_messages should be exclusively locked before invoking this method.</para>
		/// </summary>
		private void in_Receive(Message msg)
		{
			Action<Message> handler;
			bool gotHandler;
			m_logger.debugLog("looking for a handler for " + msg.DestCubeBlock.EntityId, "in_Receive()");
			using (lock_messageHandlers.AcquireSharedUsing())
				gotHandler = m_messageHandlers.TryGetValue(msg.DestCubeBlock.EntityId, out handler);

			if (gotHandler)
			{
				m_logger.debugLog("have a handler for msg", "in_Receive()");
				msg.IsValid = false;
				handler(msg);
				return;
			}

			if (m_messages.Add(msg))
				m_logger.debugLog("got a new message: " + msg.Content + ", count is now " + m_messages.Count, "receive()", Logger.severity.DEBUG);
			else
				m_logger.debugLog("already have message: " + msg.Content + ", count is now " + m_messages.Count, "receive()", Logger.severity.DEBUG);
		}

	}
}