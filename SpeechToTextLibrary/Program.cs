using NAudio.STT;
using System.Diagnostics;

namespace SpeechToTextLibrary
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("Hello, World!");

            //var vadOptions = new STTOptions
            //{
            //    VoiceDetectionThreshold = 0.025f,
            //    SpeechEndHangoverMs = 700,
            //    MinimumSpeechDurationMs = 250,
            //    SilenceTimeoutMs = 5000,
            //    SpeechStartSensitivityFrames = 2,
            //    RmsSmoothingAlpha = 0.2
            //};

            STT speech = new();
            for (int i = 0; i < 20; i++)
            {
                string text = await speech.SpeechToText() ?? null!;
                if (text == null)
                    continue;

                Console.WriteLine($"{text}");
            }
        }
    }
}
