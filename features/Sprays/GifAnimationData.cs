using UnityEngine;

namespace CatPatchspace
{
    internal sealed class GifAnimationData
    {
        public GifAnimationData(Texture2D[] frames, float[] frameDurations)
        {
            Frames = frames;
            FrameDurations = frameDurations;

            for (int i = 0; i < frameDurations.Length; i++)
                TotalDuration += Mathf.Max(GifAnimationDecoder.FallbackFrameDurationSeconds, frameDurations[i]);
        }

        public Texture2D[] Frames { get; }
        public float[] FrameDurations { get; }
        public float TotalDuration { get; }

        public bool IsValid
        {
            get { return Frames != null && Frames.Length > 0 && TotalDuration > 0f; }
        }

        public Texture2D GetFrame(double elapsedSeconds)
        {
            if (!IsValid)
                return null;

            float time = Mathf.Repeat((float)elapsedSeconds, TotalDuration);
            float cursor = 0f;

            for (int i = 0; i < Frames.Length; i++)
            {
                cursor += Mathf.Max(GifAnimationDecoder.FallbackFrameDurationSeconds, FrameDurations[i]);

                if (time <= cursor)
                    return Frames[i];
            }

            return Frames[Frames.Length - 1];
        }

        public void DestroyTextures()
        {
            if (Frames == null)
                return;

            for (int i = 0; i < Frames.Length; i++)
            {
                if (Frames[i] != null)
                    Object.Destroy(Frames[i]);
            }
        }
    }
}
