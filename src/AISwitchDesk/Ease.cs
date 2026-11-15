using System;

namespace AISwitchDesk
{
    // Timing curves shared by every rail animation. Each maps 0..1 to 0..1.
    internal static class Ease
    {
        private static float Clamp(float value)
        {
            return Math.Max(0f, Math.Min(1f, value));
        }

        internal static float SmoothStep(float value)
        {
            float t = Clamp(value);
            return t * t * (3f - 2f * t);
        }

        // Fast start, gentle stop. The default for something that responds to the pointer.
        internal static float OutCubic(float value)
        {
            float left = 1f - Clamp(value);
            return 1f - left * left * left;
        }

        // Slow at both ends. For something that travels a distance.
        internal static float InOutCubic(float value)
        {
            float t = Clamp(value);
            if (t < .5f) return 4f * t * t * t;
            float left = -2f * t + 2f;
            return 1f - left * left * left / 2f;
        }

        // Overshoots a little before it settles, like a released key.
        internal static float OutBack(float value)
        {
            const float tension = 1.55f;
            float t = Clamp(value) - 1f;
            return 1f + (tension + 1f) * t * t * t + tension * t * t;
        }
    }
}
