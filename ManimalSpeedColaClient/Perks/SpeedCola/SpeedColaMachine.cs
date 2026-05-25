using EFT.Interactive;

namespace Manimal.SpeedCola.Components
{
    // marker + interaction component on the spawned vending machine. inheriting
    // EFT.Interactive.InteractableObject means tarkov's Player.InteractionRaycast
    // resolves us via GetComponentInParent<InteractableObject> when the raycast
    // hits any collider under our root.
    //
    // the "Buy" action menu entry is built by SpeedColaActionPatch which prefixes
    // GetActionsClass.GetAvailableActions and short-circuits the vanilla dispatch
    // when it sees our type. without that patch the vanilla method throws
    // "no interactions defined for SpeedColaMachine".
    public sealed class SpeedColaMachine : InteractableObject
    {
        public string MachineId { get; private set; }

        public void Configure(string id)
        {
            MachineId = id;
        }
    }
}
