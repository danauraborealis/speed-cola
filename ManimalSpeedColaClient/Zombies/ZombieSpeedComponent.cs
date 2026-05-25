using UnityEngine;

namespace Manimal.SpeedCola.Components
{
    // marker + tunable on a zombie bot, set by ZombieSpeedAssigner at spawn.
    // ZombieClampedSpeedPatch reads this off the bot's GameObject in a
    // Harmony postfix on MovementContext.ClampedSpeed and multiplies the
    // returned speed by SpeedMultiplier - making this bot move at the
    // assigned fraction of its base speed.
    //
    // Start() also syncs the bot's Animator playback speed so the walk
    // animation timing matches the actual world-space movement speed (a
    // 0.5x bot at full-speed animation would look like moonwalking; a
    // 1.5x bot at base animation would look like sliding).
    public sealed class ZombieSpeedComponent : MonoBehaviour
    {
        public float SpeedMultiplier = 1f;

        // Animator.speed sync DISABLED. it caused some bots to freeze
        // at spawn - turns out EFT's bot locomotion blend tree gates
        // state transitions on animator-driven conditions; slowing the
        // animator below threshold means the bot never transitions out
        // of idle and stands frozen. without sync, slow zombies look a
        // little moonwalky (full-speed walk anim, half-speed motion)
        // but they actually MOVE - the trade-off is worth it.
        private void Start() { }
    }
}
