using System;
using System.Collections.Generic;
using System.Numerics;

using ACE.Entity.Enum;
using ACE.Server.Physics;
using ACE.Server.Physics.BSP;

namespace ACE.Server.Entity
{
    /// <summary>
    /// The width of a door's solid collision shape along its width axis, for the anti-blink door check
    /// (Player.CheckDoorCollision). A single global width misses wide gates: a player can cross the plane
    /// of a 7 m portcullis 3.4 m from its centre, outside a 2.5 m segment, while the server's own transition
    /// against that same closed door fails.
    ///
    /// The shape measured is the one PhysicsObj.FindObjCollisions tests a player against: the parts' physics
    /// BSP polygons when the object has HasPhysicsBSP, otherwise its cylspheres, otherwise its spheres. It is
    /// measured in the door's own frame, where the anti-blink width axis is local X (Player.GetDoorSegment
    /// takes the door's facing as local +Y and spans the perpendicular). The segment stays centred on the
    /// door's origin, so the width is twice the farthest extent from the origin on either side.
    /// </summary>
    public static class DoorCollisionWidth
    {
        /// <summary>
        /// The width the anti-blink segment is built with: the real width when it is wider than the global
        /// floor (anti_blink_door_width), otherwise the floor. A non-positive or non-finite real width (the
        /// shape could not be read) yields the floor, so no door is ever covered by less than the floor.
        /// </summary>
        public static float GetEffectiveWidth(float floorWidth, float realWidth)
        {
            if (float.IsNaN(realWidth) || float.IsInfinity(realWidth) || realWidth <= floorWidth)
                return floorWidth;

            return realWidth;
        }

        /// <summary>
        /// Twice the largest |X| over a set of points already in the door's local frame, or 0 for no points.
        /// </summary>
        public static float WidthFromDoorLocalPoints(IEnumerable<Vector3> doorLocalPoints)
        {
            var maxAbsX = 0.0f;

            foreach (var point in doorLocalPoints)
                maxAbsX = Math.Max(maxAbsX, Math.Abs(point.X));

            return maxAbsX * 2.0f;
        }

        /// <summary>
        /// A part-local vertex moved into the door's local frame, exactly as PartArray.UpdateParts places a part
        /// (AFrame.Combine with the object's Scale applied to the part frame's origin) and as PhysicsPart
        /// scales its BSP (GfxObjScale.Z, see SpherePath.CacheLocalSpaceSphere).
        /// </summary>
        public static Vector3 PartVertexToDoorLocal(Vector3 partFrameOrigin, Quaternion partFrameOrientation, Vector3 objectScale, float gfxObjScaleZ, Vector3 vertex)
        {
            return partFrameOrigin * objectScale + Vector3.Transform(vertex * gfxObjScaleZ, partFrameOrientation);
        }

        /// <summary>
        /// The real collision width of a door's physics object in its CURRENT pose, or null when the shape is
        /// not available. Call it only while the door is fully closed (not open, not ethereal): that is the
        /// pose the server's transition collides against, and an open leaf has swung off the width axis.
        /// </summary>
        public static float? Derive(PhysicsObj physicsObj)
        {
            var partArray = physicsObj?.PartArray;

            if (partArray?.Setup == null)
                return null;

            var points = new List<Vector3>();

            if (physicsObj.State.HasFlag(PhysicsState.HasPhysicsBSP))
            {
                var parts = partArray.Parts;

                if (parts == null)
                    return null;

                var animFrame = partArray.Sequence?.GetCurrAnimFrame();

                for (var i = 0; i < parts.Count; i++)
                {
                    var part = parts[i];

                    var root = part?.GfxObj?.PhysicsBSP?.RootNode;

                    if (root == null)
                        continue;

                    Vector3 frameOrigin;
                    Quaternion frameOrientation;

                    if (animFrame != null && i < animFrame.Frames.Count)
                    {
                        frameOrigin = animFrame.Frames[i].Origin;
                        frameOrientation = animFrame.Frames[i].Orientation;
                    }
                    else if (animFrame == null && parts.Count == 1)
                    {
                        // UpdateParts places a lone part at the object's own position when there is no frame
                        frameOrigin = Vector3.Zero;
                        frameOrientation = Quaternion.Identity;
                    }
                    else
                        continue;

                    var gfxObjScaleZ = part.GfxObjScale.Z;

                    foreach (var vertex in EnumerateBspVertices(root))
                        points.Add(PartVertexToDoorLocal(frameOrigin, frameOrientation, partArray.Scale, gfxObjScaleZ, vertex));
                }
            }
            else if (partArray.GetNumCylsphere() != 0)
            {
                var scale = physicsObj.Scale;

                foreach (var cylSphere in partArray.GetCylSphere())
                {
                    var reach = Math.Abs(cylSphere.LowPoint.X * scale) + cylSphere.Radius * scale;
                    points.Add(new Vector3(reach, 0, 0));
                }
            }
            else if (partArray.GetNumSphere() != 0)
            {
                var scale = physicsObj.Scale;

                foreach (var sphere in partArray.GetSphere())
                {
                    var reach = Math.Abs(sphere.Center.X * scale) + sphere.Radius * scale;
                    points.Add(new Vector3(reach, 0, 0));
                }
            }

            var width = WidthFromDoorLocalPoints(points);

            if (width <= 0.0f || float.IsNaN(width) || float.IsInfinity(width))
                return null;

            return width;
        }

        private static IEnumerable<Vector3> EnumerateBspVertices(BSPNode root)
        {
            var stack = new Stack<BSPNode>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var node = stack.Pop();

                if (node.Polygons != null)
                {
                    foreach (var polygon in node.Polygons)
                    {
                        if (polygon?.Vertices == null)
                            continue;

                        foreach (var vertex in polygon.Vertices)
                        {
                            if (vertex != null)
                                yield return vertex.Origin;
                        }
                    }
                }

                if (node.PosNode != null)
                    stack.Push(node.PosNode);

                if (node.NegNode != null)
                    stack.Push(node.NegNode);
            }
        }
    }
}
