using System;
using System.Collections.Concurrent;

namespace ACE.Server.Physics.Managers
{
    public static class ServerObjectManager
    {
        /// <summary>
        /// Custom lookup table of PhysicsObjs for the server.
        /// ACRealms port: keyed by the 64-bit (instance, guid) pair - the same static
        /// object guid can be live in multiple landblock instances at once.
        /// </summary>
        public static ConcurrentDictionary<ulong, PhysicsObj> ServerObjects { get; } = new ConcurrentDictionary<ulong, PhysicsObj>();

        private static ulong Key(uint objectID, uint instance)
        {
            return ((ulong)instance << 32) | objectID;
        }

        /// <summary>
        /// Adds a PhysicsObj to the static list of server-wide objects,
        /// registered under its KnownInstance
        /// </summary>
        public static void AddServerObject(PhysicsObj obj)
        {
            if (obj != null)
                ServerObjects[Key(obj.ID, obj.KnownInstance)] = obj;
        }

        /// <summary>
        /// Removes a PhysicsObj from the static list of server-wide objects
        /// </summary>
        public static void RemoveServerObject(PhysicsObj obj)
        {
            if (obj != null)
                ServerObjects.TryRemove(Key(obj.ID, obj.KnownInstance), out _);
        }

        /// <summary>
        /// Returns a PhysicsObj for an object ID within a specific landblock instance
        /// </summary>
        public static PhysicsObj GetObjectA(uint objectID, uint instance)
        {
            ServerObjects.TryGetValue(Key(objectID, instance), out var obj);

            return obj;
        }
    }
}
