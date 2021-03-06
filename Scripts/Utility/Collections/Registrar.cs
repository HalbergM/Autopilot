using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage;
using VRage.ModAPI;

namespace Rynchodon
{
	public static class Registrar
	{

		private static class Register<T>
		{

			//private static readonly Logger s_logger = new Logger("Register<T>", () => typeof(T).ToString());

			private static Dictionary<long, T> m_dictionary = new Dictionary<long, T>();
			private static FastResourceLock m_lock = new FastResourceLock();

			public static bool Closed { get { return m_dictionary == null; } }

			static Register()
			{
				MyAPIGateway.Entities.OnCloseAll += Entities_OnCloseAll;
			}

			static void Entities_OnCloseAll()
			{
				MyAPIGateway.Entities.OnCloseAll -= Entities_OnCloseAll;
				m_dictionary = null;
				m_lock = null;
			}

			public static void Add(long entityId, T script)
			{
				using (m_lock.AcquireExclusiveUsing())
					m_dictionary.Add(entityId, script);
				//s_logger.debugLog("Added " + script + ", for " + entityId, "Add()");
			}

			public static bool Remove(long entityId)
			{
				//s_logger.debugLog("Removing script, for " + entityId, "Remove()");
				using (m_lock.AcquireExclusiveUsing())
					return m_dictionary.Remove(entityId);
			}

			public static bool TryGetValue(long entityId, out T value)
			{
				using (m_lock.AcquireSharedUsing())
					return m_dictionary.TryGetValue(entityId, out value);
			}

			public static void ForEach(Action<T> function)
			{
				using (m_lock.AcquireSharedUsing())
					foreach (T script in m_dictionary.Values)
						function(script);
			}

			public static void ForEach(Func<T, bool> function)
			{
				using (m_lock.AcquireSharedUsing())
					foreach (T script in m_dictionary.Values)
						if (function(script))
							return;
			}

			public static bool Contains(long entityId)
			{
				using (m_lock.AcquireSharedUsing())
					return m_dictionary.ContainsKey(entityId);
			}

		}

		public static void Add<T>(IMyEntity entity, T item)
		{
			Register<T>.Add(entity.EntityId, item);
			entity.OnClose += OnClose<T>;
		}

		public static void Remove<T>(IMyEntity entity)
		{
			entity.OnClose -= OnClose<T>;
			Register<T>.Remove(entity.EntityId);
		}

		public static bool TryGetValue<T>(long entityId, out T value)
		{
			if (Globals.WorldClosed)
			{
				value = default(T);
				return false;
			}
			return Register<T>.TryGetValue(entityId, out value);
		}

		public static bool TryGetValue<T>(IMyEntity entity, out T value)
		{
			return TryGetValue(entity.EntityId, out value);
		}

		public static void ForEach<T>(Action<T> function)
		{
			Register<T>.ForEach(function);
		}

		public static void ForEach<T>(Func<T, bool> function)
		{
			Register<T>.ForEach(function);
		}

		public static bool Contains<T>(long entityId)
		{
			return Register<T>.Contains(entityId);
		}

		private static void OnClose<T>(IMyEntity obj)
		{
			if (Globals.WorldClosed)
				return;
			obj.OnClose -= OnClose<T>;
			Register<T>.Remove(obj.EntityId);
		}

	}
}
