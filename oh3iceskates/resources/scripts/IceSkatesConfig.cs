using ProtoBuf;

namespace oh3iceskates
{
    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class IceSkatesConfig
    {
        public double BlocksPerDamage = 5.0; // Default 5
        public double MaxSpeed = 0.15; // Default 0.15
        public double SlalomBoost = 0.03; // Default 0.03 (Percentage of extra acceleration added when slaloming)
        public int SlalomWindow = 700; // Default 700 (Slalom window in milliseconds)
        public double SlalomSweetspot = 0.6; // Default 0.6 (80% of the boost is spread across this percentage of the window)
        public double BiteHandling = 0.8; // Default 0.8 (Minimum handling when the skates have bite)
        public double SpeedCeiling = 0.7; // Default 0.7
        public double BrakeFriction = 0.998; // Default 0.998
        public bool DebugMode = false;

        public IceSkatesConfig()
        {
            // Empty constructor required for ProtoBuf serialization
        }
    }
}