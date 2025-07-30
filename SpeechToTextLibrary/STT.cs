using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using NAudio.Utils;
using NAudio.Wave;

//by EVOLVE
namespace NAudio.STT
{
    public class STTOptions
    {
        public float VoiceDetectionThreshold { get; set; } = 0.02f;
        public int SilenceTimeoutMs { get; set; } = 3000;
        public int SpeechStartSensitivityFrames { get; set; } = 3;
        public int SpeechEndHangoverMs { get; set; } = 500;
        public int MinimumSpeechDurationMs { get; set; } = 300;
        public double RmsSmoothingAlpha { get; set; } = 0.2;
    }

    internal class STT : IDisposable
    {
        private readonly string _flac_path, _tmp_path;
        private readonly WaveInEvent _wave_in;
        private readonly HttpClient _client;
        private readonly STTOptions _vad_options;

        public STT(int device_number = 0, string? flac_path = null, STTOptions? vad_options = null)
        {
            _tmp_path = Path.Combine(Path.GetTempPath(), "speech_temp_" + Path.GetRandomFileName());
            _flac_path = flac_path ?? "flac";
            Directory.CreateDirectory(_tmp_path);

            _vad_options = vad_options ?? new STTOptions();

            _client = new HttpClient();
            _client.DefaultRequestHeaders.ExpectContinue = false;

            _wave_in = new WaveInEvent
            {
                WaveFormat = new WaveFormat(16000, 16, 1),
                DeviceNumber = device_number,
                BufferMilliseconds = 50
            };
        }
        public async Task<string?> SpeechToText(string language = "cs-CZ")
        {
            MemoryStream? microphone_stream = null;
            try
            {
                microphone_stream = await AutomaticRecordMicrophone();
                if (microphone_stream == null || microphone_stream.Length == 0)
                {
                    //Console.WriteLine("No valid speech segment recorded.");
                    return null;
                }

                microphone_stream.Position = 0;

                string? result = await GetSpeechText(microphone_stream, language);
                return result;
            }
            finally
            {
                microphone_stream?.Dispose();
            }
        }
        public async Task<MemoryStream?> AutomaticRecordMicrophone()
        {
            var ms_output = new MemoryStream();
            var writer = new WaveFileWriter(new IgnoreDisposeStream(ms_output), _wave_in.WaveFormat);

            var tcs = new TaskCompletionSource<bool>();

            double smoothed_rms = 0.0;
            int consecutive_voice_frames = 0, current_silence_duration_ms = 0;
            bool speech_segment_started = false, valid_speech_recorded = false;
            long total_samples_written = 0;

            EventHandler<WaveInEventArgs> data_available_handler = (sender, e) =>
            {
                if (e.BytesRecorded == 0) return;
                writer.Write(e.Buffer, 0, e.BytesRecorded);
                total_samples_written += e.BytesRecorded / _wave_in.WaveFormat.BlockAlign;

                int samples = e.BytesRecorded / 2;
                double sum_squares = 0;
                for (int i = 0; i < samples; i++)
                {
                    short sample = BitConverter.ToInt16(e.Buffer, i * 2);
                    double sample32 = sample / 32768.0;
                    sum_squares += sample32 * sample32;
                }
                double rms = Math.Sqrt(sum_squares / samples);

                smoothed_rms = (_vad_options.RmsSmoothingAlpha * rms) + ((1 - _vad_options.RmsSmoothingAlpha) * smoothed_rms);
                if (smoothed_rms > _vad_options.VoiceDetectionThreshold)
                {
                    consecutive_voice_frames++;
                    current_silence_duration_ms = 0;

                    if (!speech_segment_started && consecutive_voice_frames >= _vad_options.SpeechStartSensitivityFrames)
                    {
                        speech_segment_started = true;
                    }
                }
                else
                {
                    consecutive_voice_frames = 0;
                    if (speech_segment_started)
                    {
                        current_silence_duration_ms += _wave_in.BufferMilliseconds;
                        if (current_silence_duration_ms >= _vad_options.SpeechEndHangoverMs)
                        {
                            _wave_in.StopRecording();
                        }
                    }
                    else
                    {
                        current_silence_duration_ms += _wave_in.BufferMilliseconds;
                        if (current_silence_duration_ms >= _vad_options.SilenceTimeoutMs)
                        {
                            _wave_in.StopRecording();
                        }
                    }
                }
            };

            EventHandler<StoppedEventArgs> recording_stopped_handler = (sender, e) =>
            {
                writer.Flush();

                valid_speech_recorded = speech_segment_started && writer.TotalTime.TotalMilliseconds >= _vad_options.MinimumSpeechDurationMs;

                if (!tcs.Task.IsCompleted)
                    tcs.TrySetResult(valid_speech_recorded);
            };

            _wave_in.DataAvailable += data_available_handler;
            _wave_in.RecordingStopped += recording_stopped_handler;

            try
            {
                _wave_in.StartRecording();
                valid_speech_recorded = await tcs.Task;
            }
            catch
            {
                if (!tcs.Task.IsCompleted) tcs.TrySetResult(false);
                valid_speech_recorded = false;
            }
            finally
            {
                _wave_in.DataAvailable -= data_available_handler;
                _wave_in.RecordingStopped -= recording_stopped_handler;

                writer.Dispose();
            }

            if (valid_speech_recorded)
            {
                ms_output.Position = 0;
                return ms_output;
            }
            else
            {
                ms_output.Dispose();
                return null;
            }
        }
        public async Task<string?> GetSpeechText(Stream audio_wav_stream, string language = "cs-CZ")
        {
            if (audio_wav_stream == null || audio_wav_stream.Length == 0)
            {
                return null;
            }
            audio_wav_stream.Position = 0;

            string tmp_flac = Path.Combine(_tmp_path, "voice.flac"),
                tmp_wav = Path.Combine(_tmp_path, "voice_resampled.wav");

            try
            {
                //Stopwatch conversion_watch = Stopwatch.StartNew();
                using (var reader = new WaveFileReader(audio_wav_stream))
                {
                    WaveFileWriter.CreateWaveFile(tmp_wav, reader);
                }

                await ConvertWavToFlac(tmp_wav, tmp_flac);
                //conversion_watch.Stop();

                var url = $"https://www.google.com/speech-api/v2/recognize?client=chromium&lang={language}&key=AIzaSyBOti4mM-6x9WDnZIjIeyEU21OpBXqWBgw&pFilter=0";
                byte[] flac_bytes = await File.ReadAllBytesAsync(tmp_flac);
                using var content = new ByteArrayContent(flac_bytes);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/x-flac")
                {
                    Parameters = { new System.Net.Http.Headers.NameValueHeaderValue("rate", "16000") }
                };

                //Stopwatch api_call_watch = Stopwatch.StartNew();
                var response = await _client.PostAsync(url, content);
                var json_response = await response.Content.ReadAsStringAsync();
                //api_call_watch.Stop();

                if (!response.IsSuccessStatusCode)
                    return null;

                foreach (var line in json_response.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.Contains("\"transcript\""))
                    {
                        try
                        {
                            using JsonDocument doc = JsonDocument.Parse(line);
                            JsonElement result_element = doc.RootElement.GetProperty("result");
                            if (result_element.GetArrayLength() > 0)
                            {
                                JsonElement alternativeElement = result_element[0].GetProperty("alternative");
                                if (alternativeElement.GetArrayLength() > 0)
                                {
                                    string? transcript = alternativeElement[0].GetProperty("transcript").GetString();
                                    return transcript;
                                }
                            }
                        }
                        catch
                        { }
                    }
                }
                return null;
            }
            catch
            { return null; }
            finally
            {
                if (File.Exists(tmp_wav)) File.Delete(tmp_wav);
                if (File.Exists(tmp_flac)) File.Delete(tmp_flac);
            }
        }
        public async Task ConvertWavToFlac(string wav_path, string output_flac_path)
        {
            if (!File.Exists(wav_path))
                throw new FileNotFoundException("Input WAV file not found for FLAC conversion.", wav_path);

            string flac_ex = Path.Combine(_flac_path, "flac-win32.exe");
            if (!File.Exists(flac_ex) && _flac_path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(_flac_path))
            {
                flac_ex = _flac_path;
            }
            else if (!File.Exists(flac_ex))
            {
                flac_ex = Path.Combine(_flac_path, "flac.exe");
                if (!File.Exists(flac_ex))
                    throw new FileNotFoundException($"FLAC executable not found. Looked in: {Path.Combine(_flac_path, "flac-win32.exe")} and {Path.Combine(_flac_path, "flac.exe")}. Ensure flac_path is set correctly.");
            }


            var process_info = new ProcessStartInfo
            {
                FileName = flac_ex,
                Arguments = $"--best -f -o \"{output_flac_path}\" \"{wav_path}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(process_info);
            if (process == null)
                throw new Exception("FLAC encoding process could not be started.");
            
            string std_out = await process.StandardOutput.ReadToEndAsync(),
                std_err = await process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
                throw new Exception($"FLAC encoding failed with exit code {process.ExitCode}. Error: {std_err}");
        }

        public void Dispose()
        {
            _wave_in.Dispose();
            _client.Dispose();

            try
            {
                if (Directory.Exists(_tmp_path))
                    Directory.Delete(_tmp_path, recursive: true);
            }
            catch
            { }

            GC.SuppressFinalize(this);
        }
    }
}