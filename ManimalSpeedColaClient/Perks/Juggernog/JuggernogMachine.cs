using EFT.Interactive;

namespace Manimal.SpeedCola.Components
{
    // marker + interaction component on the spawned juggernog machine. mirror
    // of SpeedColaMachine - InteractableObject so Player.InteractionRaycast
    // resolves us, with JuggernogActionPatch attaching the "Buy" action.
    public sealed class JuggernogMachine : InteractableObject
    {
        public string MachineId { get; private set; }

        public void Configure(string id)
        {
            MachineId = id;
        }
    }
}
