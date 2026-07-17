using System.Diagnostics;
using Brutal.Logging;
using Brutal.Numerics;
using KSA;
using MeasureTools.Core;

namespace MeasureTools.Features.Measure;

// Extracts CAD-style snap features from a part mesh: feature edges (boundary,
// non-manifold, or sharp: faces meeting above a dihedral threshold, e.g. a tank
// rim) and circles fitted to closed feature-edge loops. Everything is in the
// subpart-local frame of MeshReference.PositionCompare, the same triangle list
// the game's own raycast uses. Cached per MeshReference, which ModLibrary shares
// between part instances, so the build runs once per mesh type.
internal static class MeshFeatureCache
{
    // Faces meeting above this dihedral angle make their shared edge a snap
    // target; below it the edge is smooth shading detail (cylinder walls).
    private const double FeatureEdgeMinAngleDeg = 30.0;

    // Circle acceptance: enough edges to be round, radius residual and off-plane
    // deviation within a fraction of the radius. Tank rims pass; irregular
    // outlines stay plain edges.
    private const int CircleMinEdges = 8;
    private const double CircleRadiusTolerance = 0.02;
    private const double CirclePlaneTolerance = 0.02;

    internal readonly struct EdgeSegment
    {
        public readonly double3 A;
        public readonly double3 B;

        public EdgeSegment(double3 a, double3 b)
        {
            A = a;
            B = b;
        }

        public double3 Mid => (A + B) * 0.5;
    }

    internal readonly struct CircleFeature
    {
        public readonly double3 Center;
        public readonly double3 Normal;
        public readonly double Radius;

        public CircleFeature(double3 center, double3 normal, double radius)
        {
            Center = center;
            Normal = normal;
            Radius = radius;
        }
    }

    internal sealed class MeshFeatures
    {
        // The welded unique vertex positions, so the vertex snap projects each
        // position once instead of every index-unrolled duplicate.
        public readonly double3[] Vertices;
        public readonly EdgeSegment[] Edges;
        public readonly CircleFeature[] Circles;

        public MeshFeatures(double3[] vertices, EdgeSegment[] edges, CircleFeature[] circles)
        {
            Vertices = vertices;
            Edges = edges;
            Circles = circles;
        }
    }

    private static readonly Dictionary<MeshReference, MeshFeatures> _cache = new();

    // Must not touch game state (called from [StarMapUnload]).
    public static void Reset()
    {
        _cache.Clear();
    }

    public static MeshFeatures Get(MeshReference mesh)
    {
        if (_cache.TryGetValue(mesh, out MeshFeatures? features))
            return features;
        long start = Stopwatch.GetTimestamp();
        features = Build(mesh.PositionCompare);
        _cache[mesh] = features;
        if (DebugConfig.Measure)
        {
            double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            DefaultCategory.Log.Debug(
                $"[MeasureTools] Mesh features for '{mesh.Id}': {features.Edges.Length} edges, {features.Circles.Length} circles ({ms:F1} ms, {mesh.PositionCompare.Length / 3} tris).");
        }
        return features;
    }

    private struct EdgeInfo
    {
        public int FaceCount;
        public double3 Normal1;
        public double3 Normal2;
    }

    private static MeshFeatures Build(double3[] triangleVertices)
    {
        // Weld by exact position: PositionCompare entries are copies out of the
        // mesh's welded vertex buffer (MeshReference.Load), so shared edges and
        // UV-seam duplicates carry bitwise-identical positions.
        var vertexIds = new Dictionary<(double, double, double), int>();
        var positions = new List<double3>();
        int[] ids = new int[triangleVertices.Length];
        for (int i = 0; i < triangleVertices.Length; i++)
        {
            double3 p = triangleVertices[i];
            var key = (p.X, p.Y, p.Z);
            if (!vertexIds.TryGetValue(key, out int id))
            {
                id = positions.Count;
                positions.Add(p);
                vertexIds.Add(key, id);
            }
            ids[i] = id;
        }

        var edges = new Dictionary<(int, int), EdgeInfo>();
        for (int t = 0; t + 2 < triangleVertices.Length; t += 3)
        {
            double3 a = triangleVertices[t];
            double3 faceNormal = double3.Cross(triangleVertices[t + 1] - a, triangleVertices[t + 2] - a).NormalizeOrZero();
            if (faceNormal.X == 0.0 && faceNormal.Y == 0.0 && faceNormal.Z == 0.0)
                continue;
            for (int e = 0; e < 3; e++)
            {
                int v0 = ids[t + e];
                int v1 = ids[t + (e + 1) % 3];
                if (v0 == v1)
                    continue;
                (int, int) key = v0 < v1 ? (v0, v1) : (v1, v0);
                if (edges.TryGetValue(key, out EdgeInfo info))
                {
                    if (info.FaceCount == 1)
                        info.Normal2 = faceNormal;
                    info.FaceCount++;
                    edges[key] = info;
                }
                else
                {
                    edges[key] = new EdgeInfo { FaceCount = 1, Normal1 = faceNormal };
                }
            }
        }

        double sharpDotThreshold = Math.Cos(FeatureEdgeMinAngleDeg * (Math.PI / 180.0));
        var featureEdges = new List<(int V0, int V1)>();
        foreach (KeyValuePair<(int, int), EdgeInfo> pair in edges)
        {
            EdgeInfo info = pair.Value;
            // Boundary (1) and non-manifold (3+) edges are always features; a
            // manifold edge is one when its faces bend past the threshold.
            bool feature = info.FaceCount != 2
                || double3.Dot(info.Normal1, info.Normal2) < sharpDotThreshold;
            if (feature)
                featureEdges.Add(pair.Key);
        }

        var segments = new EdgeSegment[featureEdges.Count];
        for (int i = 0; i < featureEdges.Count; i++)
            segments[i] = new EdgeSegment(positions[featureEdges[i].V0], positions[featureEdges[i].V1]);

        return new MeshFeatures(positions.ToArray(), segments, FitCircles(featureEdges, positions));
    }

    // Chain feature edges through vertices with exactly two feature edges into
    // closed loops, then circle-fit each loop.
    private static CircleFeature[] FitCircles(List<(int V0, int V1)> featureEdges, List<double3> positions)
    {
        var adjacency = new Dictionary<int, List<int>>();
        for (int i = 0; i < featureEdges.Count; i++)
        {
            AddAdjacency(adjacency, featureEdges[i].V0, i);
            AddAdjacency(adjacency, featureEdges[i].V1, i);
        }

        var circles = new List<CircleFeature>();
        bool[] visited = new bool[featureEdges.Count];
        var loop = new List<int>();
        for (int start = 0; start < featureEdges.Count; start++)
        {
            if (visited[start])
                continue;
            // Walk from one endpoint; a clean loop returns to it with every
            // vertex on the way having exactly two feature edges.
            loop.Clear();
            int startVertex = featureEdges[start].V0;
            int vertex = startVertex;
            int edge = start;
            bool closed = false;
            while (true)
            {
                visited[edge] = true;
                int next = featureEdges[edge].V0 == vertex ? featureEdges[edge].V1 : featureEdges[edge].V0;
                loop.Add(next);
                if (next == startVertex)
                {
                    closed = true;
                    break;
                }
                List<int> nextEdges = adjacency[next];
                if (nextEdges.Count != 2)
                    break;
                int follow = nextEdges[0] == edge ? nextEdges[1] : nextEdges[0];
                if (visited[follow])
                    break;
                vertex = next;
                edge = follow;
            }
            if (closed && loop.Count >= CircleMinEdges && TryFitCircle(loop, positions, out CircleFeature circle))
                circles.Add(circle);
        }
        return circles.ToArray();
    }

    private static void AddAdjacency(Dictionary<int, List<int>> adjacency, int vertex, int edgeIndex)
    {
        if (!adjacency.TryGetValue(vertex, out List<int>? list))
        {
            list = new List<int>(2);
            adjacency.Add(vertex, list);
        }
        list.Add(edgeIndex);
    }

    // Newell plane normal, then an in-plane Kasa least-squares circle fit.
    // Rejects loops that are not flat circles within the tolerances.
    private static bool TryFitCircle(List<int> loop, List<double3> positions, out CircleFeature circle)
    {
        circle = default;
        int n = loop.Count;
        double3 centroid = double3.Zero;
        for (int i = 0; i < n; i++)
            centroid += positions[loop[i]];
        centroid *= 1.0 / n;

        double3 normal = double3.Zero;
        for (int i = 0; i < n; i++)
        {
            double3 current = positions[loop[i]] - centroid;
            double3 next = positions[loop[(i + 1) % n]] - centroid;
            normal += double3.Cross(current, next);
        }
        normal = normal.NormalizeOrZero();
        if (normal.X == 0.0 && normal.Y == 0.0 && normal.Z == 0.0)
            return false;

        double3 seed = Math.Abs(normal.X) < 0.9 ? new double3(1.0, 0.0, 0.0) : new double3(0.0, 1.0, 0.0);
        double3 u = double3.Cross(normal, seed).Normalized();
        double3 w = double3.Cross(normal, u).Normalized();

        // Kasa fit: minimize x^2 + y^2 + a x + b y + c over the 2D projections.
        double sxx = 0.0, sxy = 0.0, syy = 0.0, sx = 0.0, sy = 0.0;
        double sxz = 0.0, syz = 0.0, sz = 0.0;
        double maxPlaneDeviation = 0.0;
        for (int i = 0; i < n; i++)
        {
            double3 d = positions[loop[i]] - centroid;
            double x = double3.Dot(d, u);
            double y = double3.Dot(d, w);
            maxPlaneDeviation = Math.Max(maxPlaneDeviation, Math.Abs(double3.Dot(d, normal)));
            double z = x * x + y * y;
            sxx += x * x;
            sxy += x * y;
            syy += y * y;
            sx += x;
            sy += y;
            sxz += x * z;
            syz += y * z;
            sz += z;
        }
        // Solve the 3x3 normal equations for [a b c] via Cramer's rule.
        double m00 = sxx, m01 = sxy, m02 = sx;
        double m10 = sxy, m11 = syy, m12 = sy;
        double m20 = sx, m21 = sy, m22 = n;
        double r0 = -sxz, r1 = -syz, r2 = -sz;
        double det = m00 * (m11 * m22 - m12 * m21) - m01 * (m10 * m22 - m12 * m20) + m02 * (m10 * m21 - m11 * m20);
        if (Math.Abs(det) < 1e-12)
            return false;
        double a = (r0 * (m11 * m22 - m12 * m21) - m01 * (r1 * m22 - m12 * r2) + m02 * (r1 * m21 - m11 * r2)) / det;
        double b = (m00 * (r1 * m22 - m12 * r2) - r0 * (m10 * m22 - m12 * m20) + m02 * (m10 * r2 - r1 * m20)) / det;
        double c = (m00 * (m11 * r2 - r1 * m21) - m01 * (m10 * r2 - r1 * m20) + r0 * (m10 * m21 - m11 * m20)) / det;
        double cx = -a * 0.5;
        double cy = -b * 0.5;
        double radiusSq = cx * cx + cy * cy - c;
        if (!(radiusSq > 0.0))
            return false;
        double radius = Math.Sqrt(radiusSq);

        double maxRadiusResidual = 0.0;
        for (int i = 0; i < n; i++)
        {
            double3 d = positions[loop[i]] - centroid;
            double x = double3.Dot(d, u) - cx;
            double y = double3.Dot(d, w) - cy;
            maxRadiusResidual = Math.Max(maxRadiusResidual, Math.Abs(Math.Sqrt(x * x + y * y) - radius));
        }
        if (maxRadiusResidual > radius * CircleRadiusTolerance || maxPlaneDeviation > radius * CirclePlaneTolerance)
            return false;

        double3 center = centroid + u * cx + w * cy;
        circle = new CircleFeature(center, normal, radius);
        return true;
    }
}
