using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using NUnit.Framework;

namespace UnitTests.Fuzz;

/// <summary>
/// Property that a fuzzed input violated
/// </summary>
/// <param name="property">Name of the property, used to tell different bugs apart while an input is minimized</param>
/// <param name="message">Description of the violation</param>
public class FuzzFailure(string property, string message) : Exception($"{property}: {message}")
{
    /// <summary>
    /// Name of the violated property
    /// </summary>
    public string Property { get; } = property;
}

/// <summary>
/// Mutation-based fuzzer for parsers of untrusted input.
/// Every run is reproducible: DSF_FUZZ_SEED selects the random seed (default 1) and DSF_FUZZ_ITERATIONS the number of mutated inputs per test
/// </summary>
public static class Fuzzer
{
    /// <summary>
    /// Number of mutated inputs per test
    /// </summary>
    public static int Iterations => int.TryParse(Environment.GetEnvironmentVariable("DSF_FUZZ_ITERATIONS"), out int iterations) ? iterations : 5000;

    /// <summary>
    /// Random seed of the mutations
    /// </summary>
    public static int Seed => int.TryParse(Environment.GetEnvironmentVariable("DSF_FUZZ_SEED"), out int seed) ? seed : 1;

    /// <summary>
    /// Maximum time a single input may take before it counts as a hang
    /// </summary>
    private static readonly TimeSpan HangTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Maximum length of a mutated input
    /// </summary>
    private const int MaxInputLength = 4096;

    /// <summary>
    /// Maximum number of checks to spend on minimizing a failing input
    /// </summary>
    private const int MinimizeBudget = 5000;

    /// <summary>
    /// Byte values that often hit edge cases
    /// </summary>
    private static readonly byte[] InterestingBytes = [0x00, 0x01, 0x7F, 0x80, 0xFE, 0xFF, (byte)'\n', (byte)'\r', (byte)'"', (byte)'\'', (byte)'{', (byte)'}', (byte)'(', (byte)')', (byte)';', (byte)'*', (byte)'N', (byte)' '];

    /// <summary>
    /// Integer values that often hit edge cases in binary length and count fields
    /// </summary>
    private static readonly int[] InterestingInts = [0, 1, 2, 3, 4, 7, 8, 16, 0x7F, 0x80, 0xFF, 0x100, 0x7FFF, 0x8000, 0xFFFF, 0x10000, int.MaxValue, int.MinValue, -1, -2];

    /// <summary>
    /// Check the given corpus and mutations of it, then fail with a minimized input if a check throws
    /// </summary>
    /// <param name="corpus">Valid inputs to start from. Each one must pass the check as is</param>
    /// <param name="check">Check that throws if the input reveals a bug</param>
    /// <param name="dictionary">Tokens to insert into the input, such as keywords of the parsed format</param>
    /// <param name="binary">Whether the input is binary, so that integer fields are mutated and failing inputs are shown as bytes</param>
    public static void Run(IReadOnlyList<byte[]> corpus, Action<byte[]> check, IReadOnlyList<byte[]>? dictionary = null, bool binary = false)
    {
        int seed = Seed, iterations = Iterations;
        dictionary ??= [];

        byte[]? current = null, failingInput = null;
        Exception? failure = null;
        long startedAt = Stopwatch.GetTimestamp();
        int iteration = -1;

        Thread worker = new(() =>
        {
            Random random = new(seed);
            for (int i = -corpus.Count; i < iterations; i++)
            {
                byte[] input = (i < 0) ? corpus[corpus.Count + i] : Mutate(random, corpus, dictionary, binary);
                Volatile.Write(ref current, input);
                Volatile.Write(ref iteration, i);
                Interlocked.Exchange(ref startedAt, Stopwatch.GetTimestamp());
                try
                {
                    check(input);
                }
                catch (Exception e)
                {
                    failure = e;
                    failingInput = input;
                    return;
                }
            }
        })
        {
            // A hanging check cannot be aborted, so make sure it does not keep the test host alive
            IsBackground = true
        };
        worker.Start();

        while (!worker.Join(100))
        {
            if (Stopwatch.GetElapsedTime(Interlocked.Read(ref startedAt)) > HangTimeout)
            {
                Assert.Fail($"Input did not finish within {HangTimeout.TotalSeconds} s (seed {seed}, iteration {Volatile.Read(ref iteration)}):\n{Format(Volatile.Read(ref current)!, binary)}");
            }
        }

        if (failure is not null)
        {
            string signature = GetSignature(failure);
            byte[] minimized = Minimize(failingInput!, input =>
            {
                try
                {
                    check(input);
                    return false;
                }
                catch (Exception e)
                {
                    return GetSignature(e) == signature;
                }
            }, binary);

            string origin = (iteration < 0) ? "corpus entry" : $"seed {seed}, iteration {iteration}";
            Assert.Fail($"{failure.GetType().Name}: {failure.Message} ({origin})\nMinimized input:\n{Format(minimized, binary)}\nOriginal input:\n{Format(failingInput!, binary)}\n{failure}");
        }
    }

    /// <summary>
    /// Get a signature that tells different bugs apart while an input is minimized
    /// </summary>
    private static string GetSignature(Exception e) => (e is FuzzFailure ff) ? ff.Property : $"{e.GetType().FullName} at {e.TargetSite?.DeclaringType?.FullName}.{e.TargetSite?.Name}";

    /// <summary>
    /// Create a mutated input from a random corpus entry
    /// </summary>
    private static byte[] Mutate(Random random, IReadOnlyList<byte[]> corpus, IReadOnlyList<byte[]> dictionary, bool binary)
    {
        List<byte> data = [.. corpus[random.Next(corpus.Count)]];

        // Stack a few mutations, mostly one or two
        int numMutations = 1 << random.Next(4);
        for (int i = 0; i < numMutations; i++)
        {
            int position = random.Next(data.Count + 1);
            switch (random.Next(binary ? 10 : 9))
            {
                case 0:
                    // Flip a bit
                    if (data.Count > 0)
                    {
                        int index = random.Next(data.Count);
                        data[index] ^= (byte)(1 << random.Next(8));
                    }
                    break;
                case 1:
                    // Replace a byte with an interesting one
                    if (data.Count > 0)
                    {
                        data[random.Next(data.Count)] = InterestingBytes[random.Next(InterestingBytes.Length)];
                    }
                    break;
                case 2:
                    // Replace a byte with a random one
                    if (data.Count > 0)
                    {
                        data[random.Next(data.Count)] = (byte)random.Next(256);
                    }
                    break;
                case 3:
                    // Insert random bytes
                    for (int count = random.Next(1, 5); count > 0; count--)
                    {
                        data.Insert(position, (byte)random.Next(256));
                    }
                    break;
                case 4:
                    // Delete a range
                    if (data.Count > 0)
                    {
                        int start = random.Next(data.Count);
                        data.RemoveRange(start, Math.Min(random.Next(1, 9), data.Count - start));
                    }
                    break;
                case 5:
                    // Duplicate a range
                    if (data.Count > 0)
                    {
                        int start = random.Next(data.Count);
                        byte[] range = data.Skip(start).Take(random.Next(1, 17)).ToArray();
                        data.InsertRange(random.Next(data.Count + 1), range);
                    }
                    break;
                case 6:
                    // Insert a token
                    if (dictionary.Count > 0)
                    {
                        data.InsertRange(position, dictionary[random.Next(dictionary.Count)]);
                    }
                    break;
                case 7:
                    // Overwrite with a token
                    if (dictionary.Count > 0)
                    {
                        byte[] token = dictionary[random.Next(dictionary.Count)];
                        for (int j = 0; j < token.Length; j++)
                        {
                            if (position + j < data.Count)
                            {
                                data[position + j] = token[j];
                            }
                            else
                            {
                                data.Add(token[j]);
                            }
                        }
                    }
                    break;
                case 8:
                    // Splice the tail of another corpus entry
                    {
                        byte[] other = corpus[random.Next(corpus.Count)];
                        int otherStart = random.Next(other.Length + 1);
                        data.RemoveRange(position, data.Count - position);
                        data.AddRange(other.Skip(otherStart));
                    }
                    break;
                case 9:
                    // Overwrite an integer field with an interesting value
                    if (data.Count > 0)
                    {
                        int size = 1 << random.Next(3);
                        int start = (random.Next(2) == 0) ? random.Next(data.Count) : random.Next((data.Count + size - 1) / size) * size;
                        int value = InterestingInts[random.Next(InterestingInts.Length)];
                        for (int j = 0; j < size && start + j < data.Count; j++)
                        {
                            data[start + j] = (byte)(value >> (8 * j));
                        }
                    }
                    break;
            }
        }

        if (data.Count > MaxInputLength)
        {
            data.RemoveRange(MaxInputLength, data.Count - MaxInputLength);
        }
        return [.. data];
    }

    /// <summary>
    /// Remove as much from a failing input as possible while it still fails the same way
    /// </summary>
    private static byte[] Minimize(byte[] input, Func<byte[], bool> fails, bool binary)
    {
        int budget = MinimizeBudget;

        // Remove ranges of decreasing size
        for (int chunk = Math.Max(input.Length / 2, 1); chunk >= 1 && budget > 0; chunk /= 2)
        {
            for (int start = 0; start < input.Length && budget > 0;)
            {
                byte[] candidate = [.. input.AsSpan(0, start), .. input.AsSpan(Math.Min(start + chunk, input.Length))];
                budget--;
                if (fails(candidate))
                {
                    input = candidate;
                }
                else
                {
                    start += chunk;
                }
            }
        }

        // Replace the remaining bytes with neutral ones where possible
        byte neutral = binary ? (byte)0 : (byte)' ';
        for (int i = 0; i < input.Length && budget > 0; i++)
        {
            if (input[i] != neutral)
            {
                byte[] candidate = [.. input];
                candidate[i] = neutral;
                budget--;
                if (fails(candidate))
                {
                    input = candidate;
                }
            }
        }
        return input;
    }

    /// <summary>
    /// Format an input so that it can be copied into a regression test
    /// </summary>
    private static string Format(byte[] input, bool binary)
    {
        string bytes = $"new byte[] {{ {string.Join(", ", input.Select(b => $"0x{b:X2}"))} }}";
        if (binary)
        {
            return bytes;
        }

        StringBuilder text = new("\"");
        foreach (byte b in input)
        {
            text.Append(b switch
            {
                (byte)'\n' => "\\n",
                (byte)'\r' => "\\r",
                (byte)'\t' => "\\t",
                (byte)'"' => "\\\"",
                (byte)'\\' => "\\\\",
                >= 0x20 and < 0x7F => ((char)b).ToString(),
                _ => $"\\x{b:X2}"
            });
        }
        text.Append('"');
        return $"{text} (bytes: {bytes})";
    }
}
