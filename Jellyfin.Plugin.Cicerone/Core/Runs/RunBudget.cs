using System;
using System.Threading;

namespace Jellyfin.Plugin.Cicerone.Core.Runs
{
    /// <summary>
    /// The ceiling on how much audio one run may transcribe, shared by every lane.
    /// </summary>
    /// <remarks>
    /// <b>A lane holds what it is about to spend, not merely what it has spent.</b>
    /// With several items in the air at once the last thing any of them knows is what
    /// the others are about to send, so a ceiling tested against "spent so far" is no
    /// ceiling at all: every lane reads the same figure, every lane concludes there is
    /// room, and a run finishes a full round of items past the line. Reserving the
    /// estimate on the way in and settling the real figure on the way out is what
    /// makes the budget mean the same thing at one lane as at eight.
    /// <para>
    /// The test is whether the ceiling has been <em>reached</em>, not whether there is
    /// room for the whole of the next item. A budget smaller than a single item
    /// therefore stops the run after one item rather than refusing to start it, which
    /// is what the setting has always meant: it stops a run early, it does not veto
    /// one.
    /// </para>
    /// </remarks>
    public sealed class RunBudget
    {
        private readonly double _ceilingSeconds;
        private readonly Lock _gate = new();

        private double _spentSeconds;
        private double _heldSeconds;
        private bool _refused;

        /// <summary>Initialises a new instance of the <see cref="RunBudget"/> class.</summary>
        /// <param name="ceilingSeconds">The ceiling, in seconds of audio. 0 or less is no ceiling.</param>
        public RunBudget(double ceilingSeconds)
        {
            _ceilingSeconds = ceilingSeconds > 0 ? ceilingSeconds : double.PositiveInfinity;
        }

        /// <summary>Gets whether the ceiling has turned an item away.</summary>
        public bool Exhausted
        {
            get
            {
                lock (_gate)
                {
                    return _refused;
                }
            }
        }

        /// <summary>Gets the audio actually transcribed so far, in seconds.</summary>
        public double SpentSeconds
        {
            get
            {
                lock (_gate)
                {
                    return _spentSeconds;
                }
            }
        }

        /// <summary>Takes a lane's place under the ceiling.</summary>
        /// <param name="estimateSeconds">What the item is expected to cost.</param>
        /// <returns>False when the ceiling has been reached and the item must be skipped.</returns>
        public bool TryReserve(double estimateSeconds)
        {
            lock (_gate)
            {
                if (_spentSeconds + _heldSeconds >= _ceilingSeconds)
                {
                    _refused = true;
                    return false;
                }

                _heldSeconds += Math.Max(estimateSeconds, 0);
                return true;
            }
        }

        /// <summary>Releases a reservation and records what the item really cost.</summary>
        /// <param name="estimateSeconds">The figure passed to <see cref="TryReserve"/>.</param>
        /// <param name="actualSeconds">The audio the item actually transcribed.</param>
        public void Settle(double estimateSeconds, double actualSeconds)
        {
            lock (_gate)
            {
                _heldSeconds = Math.Max(0, _heldSeconds - Math.Max(estimateSeconds, 0));
                _spentSeconds += Math.Max(actualSeconds, 0);
            }
        }
    }
}
