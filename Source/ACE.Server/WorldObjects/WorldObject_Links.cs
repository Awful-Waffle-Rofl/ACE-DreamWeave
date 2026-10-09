using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Database;
using ACE.Database.Models.World;
using ACE.Database.Models.Shard;
using ACE.Entity;
using ACE.Server.Factories;

namespace ACE.Server.WorldObjects
{
    partial class WorldObject
    {
        public List<LandblockInstance> LinkedInstances = new List<LandblockInstance>();

        public WorldObject ParentLink;
        public List<WorldObject> ChildLinks = new List<WorldObject>();

        public void ActivateLinks(List<LandblockInstance> sourceObjects, List<Biota> biotas, WorldObject parent = null)
        {
            if (LinkedInstances.Count == 0) return;

            if (IsGenerator)
            {
                AddGeneratorLinks();
                return;
            }

            if (parent == null)
                parent = this;

            foreach (var link in LinkedInstances)
            {
                WorldObject wo = null;
                var biota = biotas.FirstOrDefault(b => b.Id == link.Guid);
                if (biota == null)
                    wo = WorldObjectFactory.CreateWorldObject(DatabaseManager.World.GetCachedWeenie(link.WeenieClassId), new ObjectGuid(link.Guid));
                else
                {
                    wo = WorldObjectFactory.CreateWorldObject(biota);
                    //Console.WriteLine("Loaded child biota " + wo.Name);
                }

                if (wo == null) continue;

                // Link children live in the same instance copy as their parent. World-db rows are
                // instance-agnostic, and Landblock.CreateWorldObjects stamps the instance only on the
                // top-level objects it creates - never on the children ActivateLinks builds here - so
                // a literal 0 put every lever and pressure plate of a private (ephemeral) dungeon copy
                // into the BASE world's physics landblock while its door stayed in the copy. Verified
                // live 2026-09-02 (Threads WP-1 spike, Filos' Doom 0x0150): retail wires the
                // door as the link parent and the lever/plate as the child, so the levers were missing
                // from the copy; with the parent's instance they load and operate inside it.
                wo.Location = new Position(link.ObjCellId, link.OriginX, link.OriginY, link.OriginZ, link.AnglesX, link.AnglesY, link.AnglesZ, link.AnglesW, CurrentLandblock?.Instance ?? 0);
                parent.SetLinkProperties(wo);
                CurrentLandblock?.AddWorldObject(wo);
                if (wo.PhysicsObj != null)
                    wo.PhysicsObj.Order = 0;

                wo.ParentLink = parent;
                parent.ChildLinks.Add(wo);

                // process nested links recursively
                foreach (var subLink in link.LandblockInstanceLink)
                {
                    var linkInstance = sourceObjects.FirstOrDefault(x => x.Guid == subLink.ChildGuid);

                    if (linkInstance != null)
                        wo.LinkedInstances.Add(linkInstance);
                }

                if (wo.LinkedInstances.Count > 0)
                    wo.ActivateLinks(sourceObjects, biotas);
            }
        }

        public virtual void SetLinkProperties(WorldObject wo)
        {
            // empty base
            Console.WriteLine($"{Name}.SetLinkProperties({wo.Name}) called for unknown parent type: {WeenieType}");
        }

        public virtual void UpdateLinkProperties(WorldObject wo)
        {
            // empty base
            Console.WriteLine($"{Name}.UpdateLinkProperties({wo.Name}) called for unknown parent type: {WeenieType}");
        }

        public void UpdateLinks()
        {
            foreach (var link in ChildLinks)
            {
                UpdateLinkProperties(link);

                foreach (var subLink in link.ChildLinks)
                    UpdateLinkProperties(subLink);
            }
        }
    }
}
