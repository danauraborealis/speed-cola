using EFT.Interactive;

namespace Manimal.SpeedCola.Components
{
    // marker + interaction component on the spawned Staminup machine. mirror
    // of JuggernogMachine - InteractableObject so Player.InteractionRaycast
    // resolves us, with StaminupActionPatch attaching the "Buy" action.
    public sealed class StaminupMachine : InteractableObject
    {
        public string MachineId { get; private set; }

        public void Configure(string id)
        {
            MachineId = id;
        }
    }
}
