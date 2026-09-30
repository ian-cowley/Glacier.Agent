namespace Glacier.Agent.VectorIdx;

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

/// <summary>
/// Vector distance metrics.
/// </summary>
public enum DistanceMetric
{
    SquaredL2 = 0,
    Cosine = 1,
    DotProduct = 2
}

/// <summary>
/// Hardware-accelerated vector distance kernels utilizing AVX-512, AVX2, SSE, and NEON SIMD intrinsics.
/// </summary>
public static class SimdDistance
{
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float Calculate(ReadOnlySpan<float> a, ReadOnlySpan<float> b, DistanceMetric metric)
    {
        return metric switch
        {
            DistanceMetric.SquaredL2 => SquaredL2(a, b),
            DistanceMetric.Cosine => CosineDistance(a, b),
            DistanceMetric.DotProduct => -DotProduct(a, b), // Negative dot product for min-heap distance ordering
            _ => SquaredL2(a, b)
        };
    }

    /// <summary>
    /// Computes Squared Euclidean Distance: sum((a[i] - b[i])^2).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float SquaredL2(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int length = Math.Min(a.Length, b.Length);
        int i = 0;
        float total = 0f;

        ref float pA = ref MemoryMarshal.GetReference(a);
        ref float pB = ref MemoryMarshal.GetReference(b);

        if (Vector512.IsHardwareAccelerated && length >= Vector512<float>.Count)
        {
            Vector512<float> acc512 = Vector512<float>.Zero;
            int limit = length - Vector512<float>.Count;
            while (i <= limit)
            {
                Vector512<float> va = Vector512.LoadUnsafe(ref pA, (nuint)i);
                Vector512<float> vb = Vector512.LoadUnsafe(ref pB, (nuint)i);
                Vector512<float> diff = va - vb;
                acc512 = Vector512.MultiplyAddEstimate(diff, diff, acc512);
                i += Vector512<float>.Count;
            }
            total += Vector512.Sum(acc512);
        }

        if (Vector256.IsHardwareAccelerated && (length - i) >= Vector256<float>.Count)
        {
            Vector256<float> acc256 = Vector256<float>.Zero;
            int limit = length - Vector256<float>.Count;
            while (i <= limit)
            {
                Vector256<float> va = Vector256.LoadUnsafe(ref pA, (nuint)i);
                Vector256<float> vb = Vector256.LoadUnsafe(ref pB, (nuint)i);
                Vector256<float> diff = va - vb;
                acc256 = Vector256.MultiplyAddEstimate(diff, diff, acc256);
                i += Vector256<float>.Count;
            }
            total += Vector256.Sum(acc256);
        }

        if (Vector128.IsHardwareAccelerated && (length - i) >= Vector128<float>.Count)
        {
            Vector128<float> acc128 = Vector128<float>.Zero;
            int limit = length - Vector128<float>.Count;
            while (i <= limit)
            {
                Vector128<float> va = Vector128.LoadUnsafe(ref pA, (nuint)i);
                Vector128<float> vb = Vector128.LoadUnsafe(ref pB, (nuint)i);
                Vector128<float> diff = va - vb;
                acc128 = Vector128.MultiplyAddEstimate(diff, diff, acc128);
                i += Vector128<float>.Count;
            }
            total += Vector128.Sum(acc128);
        }

        while (i < length)
        {
            float diff = Unsafe.Add(ref pA, i) - Unsafe.Add(ref pB, i);
            total += diff * diff;
            i++;
        }

        return total;
    }

    /// <summary>
    /// Computes Inner Product (Dot Product): sum(a[i] * b[i]).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float DotProduct(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int length = Math.Min(a.Length, b.Length);
        int i = 0;
        float total = 0f;

        ref float pA = ref MemoryMarshal.GetReference(a);
        ref float pB = ref MemoryMarshal.GetReference(b);

        if (Vector512.IsHardwareAccelerated && length >= Vector512<float>.Count)
        {
            Vector512<float> acc512 = Vector512<float>.Zero;
            int limit = length - Vector512<float>.Count;
            while (i <= limit)
            {
                Vector512<float> va = Vector512.LoadUnsafe(ref pA, (nuint)i);
                Vector512<float> vb = Vector512.LoadUnsafe(ref pB, (nuint)i);
                acc512 = Vector512.MultiplyAddEstimate(va, vb, acc512);
                i += Vector512<float>.Count;
            }
            total += Vector512.Sum(acc512);
        }

        if (Vector256.IsHardwareAccelerated && (length - i) >= Vector256<float>.Count)
        {
            Vector256<float> acc256 = Vector256<float>.Zero;
            int limit = length - Vector256<float>.Count;
            while (i <= limit)
            {
                Vector256<float> va = Vector256.LoadUnsafe(ref pA, (nuint)i);
                Vector256<float> vb = Vector256.LoadUnsafe(ref pB, (nuint)i);
                acc256 = Vector256.MultiplyAddEstimate(va, vb, acc256);
                i += Vector256<float>.Count;
            }
            total += Vector256.Sum(acc256);
        }

        if (Vector128.IsHardwareAccelerated && (length - i) >= Vector128<float>.Count)
        {
            Vector128<float> acc128 = Vector128<float>.Zero;
            int limit = length - Vector128<float>.Count;
            while (i <= limit)
            {
                Vector128<float> va = Vector128.LoadUnsafe(ref pA, (nuint)i);
                Vector128<float> vb = Vector128.LoadUnsafe(ref pB, (nuint)i);
                acc128 = Vector128.MultiplyAddEstimate(va, vb, acc128);
                i += Vector128<float>.Count;
            }
            total += Vector128.Sum(acc128);
        }

        while (i < length)
        {
            total += Unsafe.Add(ref pA, i) * Unsafe.Add(ref pB, i);
            i++;
        }

        return total;
    }

    /// <summary>
    /// Computes Cosine Distance: 1.0 - (dot(a, b) / (||a|| * ||b||)).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float CosineDistance(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float dot = DotProduct(a, b);
        float normA = DotProduct(a, a);
        float normB = DotProduct(b, b);

        if (normA <= 0f || normB <= 0f)
        {
            return 1.0f;
        }

        float denom = MathF.Sqrt(normA * normB);
        float sim = dot / denom;
        sim = Math.Clamp(sim, -1.0f, 1.0f);
        return 1.0f - sim;
    }
}
