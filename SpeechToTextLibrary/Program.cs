using NAudio.STT;
using System.Diagnostics;

namespace SpeechToTextLibrary
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("Hello, World!");
            STT speech = new();
            for (int i = 0; i < 20; i++)
            {
                string text = await speech.SpeechToText();
                Console.WriteLine($"{text}");
            }
        }
    }
}
