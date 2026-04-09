using ProtoBuf;

namespace oh3iceskates
{
    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class IceSkatesConfig
    {
        public double BlocksPerDamage = 5.0; // Default 5
        public double MaxSpeed = 0.15; // Default 0.15
        public double Acceleration = 0.2; // Default 0.2
        public double SkatesFriction = 0.996; // Default 0.996 (1 is Frictionless, 0 is Max Friction)
        public double SlalomBoost = 0.15; // Default 0.15 (Percentage of extra acceleration added when slaloming)
        public int SlalomWindow = 380; // Default 380 (Slalom window in milliseconds)
        public double SlalomSweetspot = 0.6; // Default 0.6 (80% of the boost is spread across this percentage of the window)
        public double BiteHandling = 0.8; // Default 0.8 (Handling override when the skates have bite)
        public double SpeedCeiling = 0.7; // Default 0.7
        public bool DebugMode = false;

        public IceSkatesConfig()
        {
            // Empty constructor required for ProtoBuf serialization
        }
    }
}