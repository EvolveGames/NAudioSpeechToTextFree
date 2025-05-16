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
            //Stream st = File.Open(@"C:\Users\Dominik\Music\ai_start.wav", FileMode.Open);
            for (int i = 0; i < 20; i++)
            {
                string text = await speech.SpeechToText();
                Console.WriteLine($"{text}");
            }
        }
    }
}
