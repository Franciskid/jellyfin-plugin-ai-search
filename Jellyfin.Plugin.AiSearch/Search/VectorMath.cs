using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.AiSearch.Search;

/// <summary>
/// The two float-array operations the index needs. Vectors are L2-normalized
/// once at index/query time so cosine similarity reduces to a dot product.
/// </summary>
public static class VectorMath
{
    /// <summary>Scales the vector to unit length (no-op for zero vectors).</summary>
    /// <param name="vector">The vector to normalize, modified in place.</param>
    public static void NormalizeInPlace(float[] vector)
    {
        double sum = 0;
        foreach (var value in vector)
        {
            sum += (double)value * value;
        }

        if (sum <= 0)
        {
            return;
        }

        var inverse = (float)(1.0 / Math.Sqrt(sum));
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] *= inverse;
        }
    }

    /// <summary>Dot product, cosine similarity for unit vectors.</summary>
    /// <param name="left">First vector.</param>
    /// <param name="right">Second vector (same length).</param>
    /// <returns>The dot product.</returns>
    public static float Dot(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        // System.Numerics.Vector<T> is part of the runtime, so this uses the
        // CPU's SIMD width (AVX2/NEON) without shipping any extra dependency.
        // Two accumulators hide the add latency; the scalar tail covers
        // dimensions that are not a multiple of the vector width.
        var length = Math.Min(left.Length, right.Length);
        var i = 0;
        float sum = 0;
        if (Vector.IsHardwareAccelerated && length >= Vector<float>.Count * 2)
        {
            var a = MemoryMarshal.Cast<float, Vector<float>>(left[..length]);
            var b = MemoryMarshal.Cast<float, Vector<float>>(right[..length]);
            var acc0 = Vector<float>.Zero;
            var acc1 = Vector<float>.Zero;
            var k = 0;
            for (; k + 1 < a.Length; k += 2)
            {
                acc0 += a[k] * b[k];
                acc1 += a[k + 1] * b[k + 1];
            }

            if (k < a.Length)
            {
                acc0 += a[k] * b[k];
            }

            sum = Vector.Dot(acc0 + acc1, Vector<float>.One);
            i = a.Length * Vector<float>.Count;
        }

        for (; i < length; i++)
        {
            sum += left[i] * right[i];
        }

        return sum;
    }
}
