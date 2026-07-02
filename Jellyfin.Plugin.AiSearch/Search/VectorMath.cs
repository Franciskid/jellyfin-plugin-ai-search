using System;

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

    /// <summary>Dot product — cosine similarity for unit vectors.</summary>
    /// <param name="left">First vector.</param>
    /// <param name="right">Second vector (same length).</param>
    /// <returns>The dot product.</returns>
    public static float Dot(float[] left, float[] right)
    {
        // Plain loop on purpose: ~3k movies × 1024 dims is a few milliseconds,
        // and it saves shipping a SIMD dependency inside the plugin folder.
        float sum = 0;
        for (var i = 0; i < left.Length; i++)
        {
            sum += left[i] * right[i];
        }

        return sum;
    }
}
