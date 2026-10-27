using CelesteMusicPlayer;

var analyzer = new SpectrumAnalyzer(40);
var dense = new float[140];
if (analyzer.TryComputeDetailed(dense, out _)) throw new Exception("Uninitialized analyzer must not invent a spectrum");
analyzer.Reset(2, 48000, 40);
var samples = new float[16384];
for (int i = 0; i < samples.Length / 2; i++) samples[i * 2] = samples[i * 2 + 1] = (float)(.5 * Math.Sin(2 * Math.PI * 1000 * i / 48000));
analyzer.Push(samples, samples.Length, 2);
if (!analyzer.TryComputeDetailed(dense, out int rate) || rate != 48000) throw new Exception("Real sample rate not returned");
int peak = Array.IndexOf(dense, dense.Max());
double low = 20 * Math.Pow(1000, peak / 140.0), high = 20 * Math.Pow(1000, (peak + 1) / 140.0);
if (low > 1100 || high < 900 || dense.Max() < .7f) throw new Exception("1 kHz tone is outside its logarithmic band");
var oldBands = new float[40];
if (!analyzer.TryCompute(oldBands) || oldBands.Max() <= 0) throw new Exception("Dense spectrum broke existing consumers");
Array.Clear(samples); analyzer.Push(samples, samples.Length, 2);
if (!analyzer.TryComputeDetailed(dense, out _) || dense.Any(v => v != 0)) throw new Exception("Silence must produce zero dense bands");
Console.WriteLine("PASS: real 1 kHz tone / 140 log bands / sample-rate identity / existing 40-band consumer preserved / silence / no-data state");
