using EFT.Interactive;

namespace Manimal.SpeedCola.Components
{
    // marker + interaction component on the spawned Quick Revive machine.
    // mirror of JuggernogMachine / StaminupMachine / DeathPerceptionMachine -
    // InteractableObject so Player.InteractionRaycast resolves us, with
    // QuickReviveActionPatch attaching the "Buy" action.
    public sealed class QuickReviveMachine : InteractableObject
    {
        public string MachineId { get; private set; }

        public void Configure(string id)
        {
            MachineId = id;
        }
    }
}
