using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace Rynchodon
{
	public static class MyPlanetExtensions
	{

		public static MyPlanet GetClosestPlanet(Vector3D position)
		{
			double distSquared;
			return GetClosestPlanet(position, out distSquared);
		}

		public static MyPlanet GetClosestPlanet(Vector3D position, out double distSquared)
		{
			IMyVoxelBase closest = null;
			double bestDistance = double.MaxValue;
			MyAPIGateway.Session.VoxelMaps.GetInstances_Safe(null, voxel => {
					if (voxel is MyPlanet)
					{
						double distance = Vector3D.DistanceSquared(position, voxel.GetCentre());
						if (distance < bestDistance)
						{
							bestDistance = distance;
							closest = voxel;
						}
					}
				return false;
			});

			distSquared = bestDistance;
			return (MyPlanet)closest;
		}

		//private static Logger s_logger = new Logger("MyPlanetExtensions");

		//private static FastResourceLock lock_getSurfPoint = new FastResourceLock("lock_getSurfPoint");

		//static MyPlanetExtensions()
		//{
		//	MyAPIGateway.Entities.OnCloseAll += Entities_OnCloseAll;
		//}

		//private static void Entities_OnCloseAll()
		//{
		//	MyAPIGateway.Entities.OnCloseAll -= Entities_OnCloseAll;
		//	s_logger = null;
		//	lock_getSurfPoint = null;
		//}

		//public static bool Intersects(this MyPlanet planet, ref BoundingSphereD sphere)
		//{
		//	Vector3D sphereCentre = sphere.Center;
		//	Vector3D planetCentre = planet.GetCentre();

		//	double distSq_sphereToPlanetCentre = Vector3D.DistanceSquared(sphereCentre, planetCentre);
		//	double everest = planet.MaximumRadius + sphere.Radius; everest *= everest;
		//	if (distSq_sphereToPlanetCentre > everest)
		//		return false;

		//	return true;

		//	Vector3D closestPoint = GetClosestSurfacePointGlobal_Safeish(planet, sphereCentre);

		//	double minDistance = sphere.Radius * sphere.Radius;
		//	if (Vector3D.DistanceSquared(sphereCentre, closestPoint) <= minDistance)
		//		return true;

		//	return distSq_sphereToPlanetCentre < Vector3D.DistanceSquared(planetCentre, closestPoint);
		//}

		//public static Vector3D GetClosestSurfacePointGlobal_Safeish(this MyPlanet planet, Vector3D worldPoint)
		//{
		//	bool except = false;
		//	Vector3D surface = Vector3D.Zero;
		//	MainLock.UsingShared(() => except = GetClosestSurfacePointGlobal_Sub_Safeish(planet, worldPoint, out surface));

		//	if (except)
		//		return GetClosestSurfacePointGlobal_Safeish(planet, worldPoint);
		//	return surface;
		//}

		//[System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
		//private static bool GetClosestSurfacePointGlobal_Sub_Safeish(this MyPlanet planet, Vector3D worldPoint, out Vector3D closestPoint)
		//{
		//	using (lock_getSurfPoint.AcquireExclusiveUsing())
		//		try
		//		{
		//			closestPoint = planet.GetClosestSurfacePointGlobal(ref worldPoint);
		//			return false;
		//		}
		//		catch (AccessViolationException ex)
		//		{
		//			s_logger.debugLog("Caught Exception: " + ex, "GetClosestSurfacePointGlobal_Sub_Safeish()", Logger.severity.DEBUG);
		//			closestPoint = Vector3D.Zero;
		//			return true;
		//		}
		//}

	}
}
