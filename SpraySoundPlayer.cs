using System.Collections;
using System.IO;
using BepInEx;
using UnityEngine;
using UnityEngine.Networking;

namespace CatPatchspace
{
    internal sealed class SpraySoundPlayer : MonoBehaviour
    {
        private AudioClip _sprayClip;
        private AudioSource _audioSource;
        private bool _loading;

        private void Awake()
        {
            _audioSource = gameObject.AddComponent<AudioSource>();
            _audioSource.playOnAwake = false;
            _audioSource.spatialBlend = 0f;
            _audioSource.volume = 0.8f;

            StartCoroutine(LoadSpraySound());
        }

        public void PlaySprayRequestSound()
        {
            if (_sprayClip == null || _audioSource == null)
                return;

            _audioSource.PlayOneShot(_sprayClip);
        }

        private IEnumerator LoadSpraySound()
        {
            if (_loading)
                yield break;

            _loading = true;

            string path = Path.Combine(
                Paths.ConfigPath,
                "Cat-Patch",
                "sounds",
                "spray.mp3");

            if (!File.Exists(path))
            {
                Plugin.Logger.LogWarning("Spray sound not found at: " + path);
                yield break;
            }

            string url = "file:///" + path.Replace("\\", "/");

            using (UnityWebRequest request =
                   UnityWebRequestMultimedia.GetAudioClip(url, AudioType.MPEG))
            {
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Plugin.Logger.LogWarning("Failed to load spray sound: " + request.error);
                    yield break;
                }

                _sprayClip = DownloadHandlerAudioClip.GetContent(request);
            }
        }
    }
}