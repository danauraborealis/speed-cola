using EFT.Interactive;

namespace Manimal.SpeedCola.Components
{
    // marker + interaction component on the spawned Death Perception machine.
    // mirror of JuggernogMachine / StaminupMachine - InteractableObject so
    // Player.InteractionRaycast resolves us, with DeathPerceptionActionPatch
    // attaching the "Buy" action.
    public sealed class DeathPerceptionMachine : InteractableObject
    {
        public string MachineId { get; private set; }

        public void Configure(string id)
        {
            MachineId = id;
        }
    }
}
