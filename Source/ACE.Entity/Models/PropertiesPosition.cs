using System;

namespace ACE.Entity.Models
{
    public class PropertiesPosition
    {
        public uint ObjCellId { get; set; }

        /// <summary>
        /// The landblock instance this position belongs to. Null means instance 0 (the base world).
        /// </summary>
        public uint? Instance { get; set; }

        public float PositionX { get; set; }
        public float PositionY { get; set; }
        public float PositionZ { get; set; }
        public float RotationW { get; set; }
        public float RotationX { get; set; }
        public float RotationY { get; set; }
        public float RotationZ { get; set; }

        public PropertiesPosition Clone()
        {
            var result = new PropertiesPosition
            {
                ObjCellId = ObjCellId,
                Instance = Instance,
                PositionX = PositionX,
                PositionY = PositionY,
                PositionZ = PositionZ,
                RotationW = RotationW,
                RotationX = RotationX,
                RotationY = RotationY,
                RotationZ = RotationZ,
            };

            return result;
        }
    }
}
