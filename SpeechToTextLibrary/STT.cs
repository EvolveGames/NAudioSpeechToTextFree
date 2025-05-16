using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using NAudio.Utils;
using NAudio.Wave;

//by EVOLVE
namespace NAudio.STT
{
    internal class STT: IDisposable
    {
        private readonly string flac_path, tmp_path;
        public float silence_threshold { get; private set; } = 0.03f; float _silence_threshold;
        WaveInEvent wave_in;
        HttpClient client;

        public STT(int device_number = default, string flac_path = null!)
        {
            tmp_path = Path.Combine(Path.GetTempPath(), "speech");
            this.flac_path = flac_path ?? "flac";
            Directory.CreateDirectory(tmp_path);
            _silence_threshold = silence_threshold;

            client = new HttpClient();
            client.DefaultRequestHeaders.ExpectContinue = false;

            wave_in = new WaveInEvent
            {
                WaveFormat = new WaveFormat(16000, 16, 1),
                DeviceNumber = device_number
            };
        }

        public async Task<string> SpeechToText(int silence_timeout_ms = 300, string language = "cs-CZ")
        {
            string result = null!;
            float default_silence_threshold = silence_threshold;
            int stream_empty_count = 0;
            for(; ; )
            {
                Console.WriteLine($"{_silence_threshold} . {stream_empty_count}");
                MemoryStream microphone_stream = await AutomaticRecordMicrophone(silence_timeout_ms, _silence_threshold);
                if (microphone_stream == null)
                { 
                    stream_empty_count++;

                    if (_silence_threshold > default_silence_threshold)
                        _silence_threshold -= 0.0001f;

                    continue;
                }

                //microphone_stream = TrimSilence(microphone_stream);

                result = await GetSpeechText(microphone_stream, language);
                if (!string.IsNullOrEmpty(result))
                { stream_empty_count = 0; break; }

                _silence_threshold += 0.001f;
                stream_empty_count = 0;
            }
            return result;
        }
        public async Task<MemoryStream> AutomaticRecordMicrophone(int silence_timeout_ms = 1000, float silence_threshold = 0.02f)
        {
            var ms_output = new MemoryStream();

            var writer = new WaveFileWriter(new IgnoreDisposeStream(ms_output), wave_in.WaveFormat);

            var tcs = new TaskCompletionSource();
            int silence_duration = 0;
            bool voice_detected = false;

            double smoothed_rms = 0, smoothing_factor = 0.9;

            int active_frames = 0;
            const int required_active_frames = 3;

            EventHandler<WaveInEventArgs> data_available_handler = (s, e) =>
            {
                writer.Write(e.Buffer, 0, e.BytesRecorded);

                int bytes_per_sample = 2, samples = e.BytesRecorded / bytes_per_sample;
                double sum_squares = 0;

                for (int i = 0; i < samples; i++)
                {
                    short sample = BitConverter.ToInt16(e.Buffer, i * bytes_per_sample);
                    double sample32 = sample / 32768.0;
                    sum_squares += sample32 * sample32;
                }

                double rms = Math.Sqrt(sum_squares / samples);
                smoothed_rms = (smoothing_factor * smoothed_rms) + ((1 - smoothing_factor) * rms);
                //Console.WriteLine(smoothed_rms);
                if (smoothed_rms > silence_threshold)
                {
                    active_frames++;
                    if (active_frames >= required_active_frames)
                    {
                        voice_detected = true;
                        silence_duration = 0;
                    }
                }
                else
                {
                    active_frames = 0;
                    silence_duration += wave_in.BufferMilliseconds;

                    if (silence_duration >= silence_timeout_ms)
                    {
                        wave_in.StopRecording();
                    }
                }
            };

            EventHandler<StoppedEventArgs> recording_stopped_handler = (s, e) =>
            {
                writer.Dispose();
                wave_in.Dispose();
                tcs.SetResult();
            };

            wave_in.DataAvailable += data_available_handler;
            wave_in.RecordingStopped += recording_stopped_handler;

            wave_in.StartRecording();
            await tcs.Task;

            wave_in.DataAvailable -= data_available_handler;
            wave_in.RecordingStopped -= recording_stopped_handler;

            ms_output.Position = 0;
            return voice_detected ? ms_output : null!;
        }
        public async Task<string> GetSpeechText(Stream audio_wav, string language = "cs-CZ")
        {
            if (audio_wav == null)
                return null!;

            Stopwatch _st_watch = Stopwatch.StartNew();
            audio_wav.Position = 0;
            string tmp_flac = Path.Combine(tmp_path, "voice.flac");
            string tmp_wav = Path.Combine(tmp_path, "voice.wav");

            using var reader = new WaveFileReader(audio_wav);

            var outFormat = new WaveFormat(16000, 16, 1);
            using var resampler = new MediaFoundationResampler(reader, outFormat)
            {
                ResamplerQuality = 60
            };

            WaveFileWriter.CreateWaveFile(tmp_wav, resampler);
            await ConvertWavToFlac(tmp_wav, tmp_flac);
            _st_watch.Stop();


            var url = $"http://www.google.com/speech-api/v2/recognize?client=chromium&lang={language}&key=AIzaSyBOti4mM-6x9WDnZIjIeyEU21OpBXqWBgw&pFilter=0";

            var bytes = await File.ReadAllBytesAsync(tmp_flac);
            using var ms = new MemoryStream(bytes);
            using var content = new StreamContent(ms);
            content.Headers.TryAddWithoutValidation("Content-Type", "audio/x-flac; rate=16000");

            Stopwatch _ls_watch = Stopwatch.StartNew();
            var response = await client.PostAsync(url, content);
            var json = await response.Content.ReadAsStringAsync();

            _ls_watch.Stop();
            Console.WriteLine($"{_st_watch.ElapsedMilliseconds} MS  => {_ls_watch.ElapsedMilliseconds} MS");

            foreach (var line in json.Split('\n'))
            {
                if (line.Contains("\"transcript\""))
                {
                    using var doc = JsonDocument.Parse(line);
                    var transcript = doc.RootElement
                        .GetProperty("result")[0]
                        .GetProperty("alternative")[0]
                        .GetProperty("transcript")
                        .GetString();
                    return transcript ?? null!;
                }
            }

            return null!;
        }
        public async Task ConvertWavToFlac(string path_wav, string output_path_flac)
        {
            if(File.Exists(path_wav))
            {
                var process = Process.Start(new ProcessStartInfo() 
                {
                    FileName = Path.Combine(flac_path, "flac-win32.exe"),
                    Arguments = $"--best -f -o \"{output_path_flac}\" \"{path_wav}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false
                });

                if (process == null)
                    throw new Exception($"FLAC encoding failed");

                await process.WaitForExitAsync();
                
                if (process.ExitCode != 0)
                {
                    throw new Exception($"FLAC encoding failed");
                }
            }
           
        }
        public void Dispose()
        {
            try
            { 
                client.Dispose();
                if (Directory.Exists(tmp_path)) Directory.Delete(tmp_path, recursive: true);
            }
            catch
            { }
        }
    }
}
