using NAudio.Dsp;
using NAudio.Vorbis;
using PersistentCollection;
using Pickles_Playlist_Editor.Tools;
using System;
using System.IO;

namespace Pickles_Playlist_Editor.Utils
{
    internal class KeyDetector
    {
        private static readonly string s_dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Pickles Playlist Editor");

        private static PersistentDictionary<string, string> keyCache;

        private const int WindowSize = 8192;
        private const int AnalysisSeconds = 20;
        private const double MinFreq = 55.0;   // A1
        private const double MaxFreq = 1000.0; // B5 — favors chord tones over high melodic decoration/harmonics

        private static readonly string[] NoteNames =
            { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        // Krumhansl-Kessler key profiles
        private static readonly double[] MajorProfile =
            { 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88 };
        private static readonly double[] MinorProfile =
            { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17 };

        static KeyDetector()
        {
            string dataDir = EnsureDataDir();
            keyCache = TryOpenOrRecreate(Path.Combine(dataDir, "key_cache.dat"));
        }

        private static PersistentDictionary<string, string> TryOpenOrRecreate(string path)
        {
            try
            {
                return new PersistentDictionary<string, string>(path);
            }
            catch
            {
                // Cache file may be corrupt or from an incompatible version — delete and retry.
                try { File.Delete(path); } catch { }
                try { return new PersistentDictionary<string, string>(path); } catch { }
                return null;
            }
        }

        private static string EnsureDataDir()
        {
            Directory.CreateDirectory(s_dataDir);
            return s_dataDir;
        }

        internal static string GetKeyFromSCD(string scdFile)
        {
            try
            {
                string path = Path.Combine(Settings.PenumbraLocation, Settings.ModName, scdFile);

                if (keyCache.ContainsKey(path))
                {
                    return keyCache[path];
                }

                string tmpOgg = Path.Combine(Path.GetTempPath(), "temp_extracted_key.ogg");
                try
                {
                    ScdOggExtractor.ExtractOgg(path, tmpOgg);
                    string key = DetectKeyFromFile(tmpOgg);
                    keyCache[path] = key;
                    return key;
                }
                finally
                {
                    try { File.Delete(tmpOgg); } catch { }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                return string.Empty;
            }
        }

        internal static string TryGetCachedKey(string scdFile)
        {
            try
            {
                string path = Path.Combine(Settings.PenumbraLocation, Settings.ModName, scdFile);
                return keyCache.ContainsKey(path) ? keyCache[path] : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        internal static void UpdateCacheForSCD(string oldFullPath, string newFullPath)
        {
            try
            {
                if (keyCache.ContainsKey(oldFullPath))
                {
                    keyCache[newFullPath] = keyCache[oldFullPath];
                    keyCache.Remove(oldFullPath);
                }
            }
            catch (Exception ex)
            {
                // ignore cache update failures
            }
        }

        private static string DetectKeyFromFile(string oggFile)
        {
            try
            {
                using var reader = new VorbisWaveReader(oggFile);
                double[] chroma = ComputeChroma(reader);
                return CorrelateProfile(chroma);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                return string.Empty;
            }
        }

        private static double[] ComputeChroma(VorbisWaveReader reader)
        {
            NAudio.Wave.ISampleProvider sampleProvider = reader;
            int channels = reader.WaveFormat.Channels;
            int sampleRate = reader.WaveFormat.SampleRate;
            int hopSize = WindowSize / 2;
            int maxSamples = sampleRate * AnalysisSeconds;

            var chroma = new double[12];
            var readBuffer = new float[WindowSize * channels];
            // Persistent buffer; the first carryCount samples are the overlap seed from the previous window.
            var mono = new float[WindowSize];
            var fftBuffer = new Complex[WindowSize];
            int m = (int)Math.Log(WindowSize, 2.0);

            int carryCount = 0;
            int totalSamplesRead = 0;

            while (totalSamplesRead < maxSamples)
            {
                int framesToRead = WindowSize - carryCount;
                int samplesRead = sampleProvider.Read(readBuffer, 0, framesToRead * channels);
                if (samplesRead <= 0)
                    break;

                int framesRead = samplesRead / channels;
                for (int i = 0; i < framesRead; i++)
                {
                    float sum = 0f;
                    for (int c = 0; c < channels; c++)
                        sum += readBuffer[i * channels + c];
                    mono[carryCount + i] = sum / channels;
                }
                totalSamplesRead += framesRead;

                int available = carryCount + framesRead;
                if (available < WindowSize)
                {
                    carryCount = available;
                    continue;
                }

                for (int i = 0; i < WindowSize; i++)
                {
                    float windowed = mono[i] * (float)FastFourierTransform.HannWindow(i, WindowSize);
                    fftBuffer[i].X = windowed;
                    fftBuffer[i].Y = 0f;
                }

                FastFourierTransform.FFT(true, m, fftBuffer);
                AccumulateChroma(fftBuffer, sampleRate, chroma);

                // Keep the second half of this window as the overlap seed for the next one.
                Array.Copy(mono, hopSize, mono, 0, WindowSize - hopSize);
                carryCount = WindowSize - hopSize;
            }

            double max = 0;
            for (int i = 0; i < 12; i++)
                max = Math.Max(max, chroma[i]);
            if (max > 0)
                for (int i = 0; i < 12; i++)
                    chroma[i] /= max;

            return chroma;
        }

        private static void AccumulateChroma(Complex[] fftBuffer, int sampleRate, double[] chroma)
        {
            int n = fftBuffer.Length;
            for (int bin = 1; bin < n / 2; bin++)
            {
                double freq = bin * (double)sampleRate / n;
                if (freq < MinFreq || freq > MaxFreq)
                    continue;

                double magnitude = Math.Sqrt(fftBuffer[bin].X * fftBuffer[bin].X + fftBuffer[bin].Y * fftBuffer[bin].Y);
                // Semitones above A4 (440Hz), converted to a C-rooted pitch class (A is 9 semitones above C).
                int semitoneFromA = (int)Math.Round(12 * Math.Log(freq / 440.0, 2.0));
                int pitchClass = ((semitoneFromA + 9) % 12 + 12 * 100) % 12;
                chroma[pitchClass] += magnitude;
            }
        }

        private static string CorrelateProfile(double[] chroma)
        {
            double best = double.NegativeInfinity;
            int bestRoot = 0;
            bool bestIsMinor = false;

            for (int root = 0; root < 12; root++)
            {
                double majorScore = Correlate(chroma, MajorProfile, root);
                if (majorScore > best)
                {
                    best = majorScore;
                    bestRoot = root;
                    bestIsMinor = false;
                }

                double minorScore = Correlate(chroma, MinorProfile, root);
                if (minorScore > best)
                {
                    best = minorScore;
                    bestRoot = root;
                    bestIsMinor = true;
                }
            }

            return $"{NoteNames[bestRoot]} {(bestIsMinor ? "Minor" : "Major")}";
        }

        private static double Correlate(double[] chroma, double[] profile, int root)
        {
            double meanChroma = 0, meanProfile = 0;
            for (int i = 0; i < 12; i++)
            {
                meanChroma += chroma[i];
                meanProfile += profile[i];
            }
            meanChroma /= 12;
            meanProfile /= 12;

            double num = 0, denomChroma = 0, denomProfile = 0;
            for (int i = 0; i < 12; i++)
            {
                double c = chroma[i] - meanChroma;
                double p = profile[(i - root + 12) % 12] - meanProfile;
                num += c * p;
                denomChroma += c * c;
                denomProfile += p * p;
            }

            double denom = Math.Sqrt(denomChroma * denomProfile);
            return denom == 0 ? 0 : num / denom;
        }
    }
}
