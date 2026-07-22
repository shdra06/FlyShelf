// ---------------------------------------------------------------
// SoundEffects — Optional audio feedback for transfers and connections
// Uses System.Media.SystemSounds (no custom WAV files needed)
// ---------------------------------------------------------------
using System;

namespace FlyShelf.Classes
{
    public static class SoundEffects
    {
        private static bool _enabled = true;
        public static bool Enabled { get => _enabled; set => _enabled = value; }

        public static void PlayTransferComplete()
        {
            if (!_enabled) return;
            try { System.Media.SystemSounds.Asterisk.Play(); }
            catch { /* Audio playback failures are non-critical */ }
        }

        public static void PlayTransferStart()
        {
            if (!_enabled) return;
            try { System.Media.SystemSounds.Exclamation.Play(); }
            catch { /* Audio playback failures are non-critical */ }
        }

        public static void PlayDeviceConnected()
        {
            if (!_enabled) return;
            try { System.Media.SystemSounds.Hand.Play(); }
            catch { /* Audio playback failures are non-critical */ }
        }

        public static void PlayError()
        {
            if (!_enabled) return;
            try { System.Media.SystemSounds.Beep.Play(); }
            catch { /* Audio playback failures are non-critical */ }
        }
    }
}
