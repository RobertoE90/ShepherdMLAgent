using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Unity.Mathematics;
using UnityEngine;
using Random = UnityEngine.Random;

public class LoopedLandMesh
{
    private Transform _parentTransform;
    private GameObject _meshGameObject;
    private Thread _meshingThread;
    
    //private byte[] _textureData;
    private float _worldHeight;
    private Vector2 _worldSpaceArea;

    private ClusterMetaData _clusterMetadata;
    private TextureDataUtility _textureDataUtility;
    private List<Vector2> _maskLoop;

    private int _verticalLoopsCount = 0;
    private int[] _decimateRandomDeltas;
    private List<Vector2[]> _horizontalLoops;

    private Vector3[] _meshPoints;
    private List<int> _triangles;
    private Mesh _mesh;

    private bool _updateMeshFlag = false;
    private bool _killThreadFlag = false;
    
    public LoopedLandMesh(ClusterMetaData metadata, int verticalMeshResolution, float worldHeight, Vector2 worldSpaceArea, Transform parent)
    {
        _parentTransform = parent;
        _meshGameObject = new GameObject("LandMesh");
        _meshGameObject.transform.SetParent(_parentTransform);

        var meshRender = _meshGameObject.AddComponent<MeshFilter>();
        AddMaterial();
        
        _worldHeight = worldHeight;
        _worldSpaceArea = worldSpaceArea;
        
        _clusterMetadata = metadata;

        _verticalLoopsCount = verticalMeshResolution;
        _horizontalLoops = new List<Vector2[]>();

        _decimateRandomDeltas = new int[_verticalLoopsCount];
        bool verticalDecrement = Random.value > 0.5f;
        var initValue = Random.Range(20, 30);
        if (!verticalDecrement)
            initValue = Random.Range(3, 7);

        var cad = "";
        for (var i = 0; i < _verticalLoopsCount; i++)
        {
            var nextValue = initValue + (Random.Range(3, 10) * (verticalDecrement ? -1 : 1));
            _decimateRandomDeltas[i] = Mathf.Clamp(nextValue, 3, 50);
            initValue = nextValue;
            cad += $"{_decimateRandomDeltas[i]}  ";
        }

        Debug.Log($"aaa {verticalDecrement}: {cad}");

        _mesh = new Mesh();
        meshRender.mesh = _mesh;
        
        _triangles = new List<int>();
        _meshingThread = new Thread(ComputeHorizontalLoopProcess);
    }

    private void AddMaterial()
    {
        var mat = new Material(Shader.Find("Universal Render Pipeline/Simple Lit"));
        var renderer = _meshGameObject.AddComponent<MeshRenderer>();
        renderer.material = mat;
    }
    
    public void UpdateTextureData(byte[] textureData, int2 texturePixelSize)
    {
        var textureDataCopy = new byte[texturePixelSize.x * texturePixelSize.y * 3]; //only 3 channels
        for (var y = 0; y < texturePixelSize.y; y++)
        {
            for (var x = 0; x < texturePixelSize.x; x++)
            {
                var iOriginal = (x + y * texturePixelSize.x) * 4;
                var iCopy = (x + y * texturePixelSize.x) * 3;
                textureDataCopy[iCopy + (int)MapChannelCode.Height] = textureData[iOriginal + (int)MapChannelCode.Height];
                textureDataCopy[iCopy + (int)MapChannelCode.ClusterId] = textureData[iOriginal + (int)MapChannelCode.ClusterId];
            }
        }

        _textureDataUtility = new TextureDataUtility(textureDataCopy, texturePixelSize, 3);
        _meshingThread.Start();
    }

    private void ComputeHorizontalLoopProcess()
    {
        
        ExpandClusterBorders();
        var edges = ComputeBoundEdges();
        ComputeMaskLoop(new HashSet<int2>(edges));

        var pointCount = (int)(_maskLoop.Count * 0.15f);
        
        for (var i = 0; i < _verticalLoopsCount; i++)
        {
            var decimatedLoop = DecimateLoop(_maskLoop,pointCount + _decimateRandomDeltas[i]);
            _horizontalLoops.Add(decimatedLoop);
        }
        
        Thread.Yield();

        _meshPoints = GenerateMeshPoints().ToArray();
       // _mesh.SetVertices(GenerateMeshPoints());
       TriangulateMeshLoops();
        _updateMeshFlag = true;
        
    }

    private List<Vector3> GenerateMeshPoints()
    {
        var points = new List<Vector3>();
        var hDelta =  _worldHeight * (float)(_clusterMetadata.MaxValue / 255f) / (_horizontalLoops.Count - 1);

        var hIndex = 0;
        foreach (var loop in _horizontalLoops)
        {
            foreach (var p in loop)
                points.Add(new Vector3(p.x, hDelta * hIndex, p.y));

            hIndex++;
        }
        return points;
    }

    private void ExpandClusterBorders()
    {
        var bounds = _clusterMetadata.Bounds;
        var rectBeginPosition = new int2((int)bounds.position.x, (int)bounds.position.y);
        var searchSpace = new int2[]
        {
            new int2(-1, -1),
            new int2(0, -1),
            new int2(1, -1),

            new int2(-1, 0),
            new int2(1, 0),

            new int2(-1, 1),
            new int2(0, 1),
            new int2(1, 1),
        };

        var expandIndexesList = new List<int>();
        int operationIndex = 0;
        for (var y = -1; y <= bounds.height; y++)
        {
            for (var x = -1; x <= bounds.width; x++)
            {
                var neighborCounter = 0;
                foreach (var delta in searchSpace)
                {
                    if(_textureDataUtility.IsPixelInRange(
                        new int2(x, y) + delta + rectBeginPosition, 
                        out operationIndex))
                    {
                        var neighborValues = _textureDataUtility.GetPixelValues(operationIndex);
                        if (neighborValues[(int) MapChannelCode.ClusterId] == _clusterMetadata.ClusterId)
                            neighborCounter++;
                    }
                }

                if (_textureDataUtility.IsPixelInRange(
                    new int2(x, y) + rectBeginPosition,
                    out operationIndex))
                {
                    if(neighborCounter != 0 && neighborCounter != 8)
                        expandIndexesList.Add(operationIndex);
                }
            }
        }
        Thread.Yield();
        
        foreach (var index in expandIndexesList)
            _textureDataUtility.UpdateTextureData(_clusterMetadata.ClusterId, index, MapChannelCode.ClusterId);
        
        Thread.Yield();
    }
    
    private HashSet<int2> ComputeBoundEdges()
    {
        var searchRect = _clusterMetadata.Bounds;
        var edges = new HashSet<int2>();
        
        var pivotPoint = int2.zero;
        for (int j = 0; j < 2; j++) //horizontal search
        {
            var searchRow = j == 0 ? 0 : (int) searchRect.height;
            for (var it = 0; it <= searchRect.width; it++)
            {
                pivotPoint = new int2(it, searchRow);
                pivotPoint += new int2((int)searchRect.position.x, (int)searchRect.position.y);
                ClusterSearchForPixel(pivotPoint);
                
                Thread.Yield();
            }
        }
        
        for (int j = 0; j < 2; j++) //vertical search
        {
            var searchCollum = j == 0 ? 0 : (int) searchRect.width;
            for (var it = 0; it <= searchRect.height; it++)
            {
                pivotPoint = new int2(searchCollum, it);
                pivotPoint += new int2((int)searchRect.position.x, (int)searchRect.position.y);
                ClusterSearchForPixel(pivotPoint);
                
                Thread.Yield();
            }
        }

        return edges;
        void ClusterSearchForPixel(int2 pivotPoint)
        {
            var inRange = _textureDataUtility.IsPixelInRange(pivotPoint, out var index);
            if (!inRange)
                return;

            var pValues = _textureDataUtility.GetPixelValues(index);
            if (pValues[(int) MapChannelCode.ClusterId] == _clusterMetadata.ClusterId)
            {
                edges.Add(pivotPoint);
                return;
            } 
            
            if (pValues[(int)MapChannelCode.Visited] != 0)
                return;
            
            var clusterQueue = new Queue<int2>();
            clusterQueue.Enqueue(pivotPoint);

            while (true)
            {
                if (clusterQueue.Count == 0)
                {   
                    //its finish
                    break;
                }
                
                var currentPosition = clusterQueue.Dequeue();
                _textureDataUtility.IsPixelInRange(currentPosition, out var currentIndex);
                var currentPixel = _textureDataUtility.GetPixelValues(currentIndex);
                
                if(currentPixel[(int)MapChannelCode.Visited] != 0) //don't process if it has passed
                    continue;
                
                for(var it = 0; it < _textureDataUtility.GetNeighborSearchDirectionsCount(); it++)
                {
                    var sd = _textureDataUtility.GetNeighborSearchDirectionAt(it);
                    var neighborPos = currentPosition + sd;
                    if (!searchRect.Contains(new Vector2(neighborPos.x, neighborPos.y)))
                        continue;
                    
                    if(!_textureDataUtility.IsPixelInRange(neighborPos, out var neighborIndex))
                        continue;

                    var neighborValues = _textureDataUtility.GetPixelValues(neighborIndex);

                    if (neighborValues[(int) MapChannelCode.ClusterId] != _clusterMetadata.ClusterId) //expanding cluster
                    {
                        clusterQueue.Enqueue(neighborPos);
                        edges.Remove(neighborPos);
                    }
                    else
                    {
                        edges.Add(neighborPos);
                    }
                }
                
                _textureDataUtility.UpdateTextureData(byte.MaxValue, currentIndex, MapChannelCode.Visited);
            }
        }
    }

    private void ComputeMaskLoop(HashSet<int2> edges)
    {
        
        var pivot = edges.First();
        edges.Remove(pivot);
        _maskLoop = new List<Vector2>();
        _maskLoop.Add(FromTextureToWorldSpace(pivot));
        var searchSpace = new int2[]
        {
            new int2(-1, 0),
            new int2(-1, 1),
            new int2(0, 1),
            new int2(1, 1),
            new int2(1, 0),
            new int2(1, -1),
            new int2(0, -1),
            new int2(-1, -1),
        };
        
        while (true)
        {
            bool foundConnection = false;
            foreach (var delta in searchSpace)
            {
                var searchPoint = pivot + delta;
                if (edges.Remove(searchPoint))
                {
                    pivot = searchPoint;
                    
                    _maskLoop.Add(FromTextureToWorldSpace(pivot));
                    foundConnection = true;
                    break;
                }
            }
            
            if(!foundConnection)
                break;
        }

        Thread.Yield();

        Vector2 FromTextureToWorldSpace(int2 textureSpacePosition)
        {
            var normalizedCoord = new Vector2(
                (float) textureSpacePosition.x / _textureDataUtility.TextureSize.x,
                (float) textureSpacePosition.y / _textureDataUtility.TextureSize.y);

            normalizedCoord -= Vector2.one * 0.5f;
            return Vector2.Scale(normalizedCoord, _worldSpaceArea);
        }
    }
    
    private Vector2[] DecimateLoop(List<Vector2> loop, int itemCountTarget)
    {
        if (itemCountTarget < 3)
        {
            Debug.LogWarning("Trying to decimate under a polygon");
            return loop.ToArray();
        }

        if (loop.Count <= itemCountTarget)
        {
            Debug.LogWarning($"Trying to decimate to {itemCountTarget} with an array of {loop.Count}");
            return loop.ToArray();
        }

        var decimateValues = new List<IndexAndDecimateValue>(loop.Count);

        for (var i = 1; i < loop.Count; i++)
        {
            var edgeA = loop[GetLoopedIndex(i, loop.Count)] - loop[GetLoopedIndex(i - 1, loop.Count)];
            var edgeB = loop[GetLoopedIndex(i, loop.Count)] - loop[GetLoopedIndex(i + 1, loop.Count)];

            decimateValues.Add(new IndexAndDecimateValue
            {
                Index = i,
                DecimateValue = Math.Abs(Vector2.Dot(edgeA, edgeB))
            });
        }
        decimateValues.Sort();
        
        var toDeleteHash = new HashSet<int>();
        for (var i = 0; i < loop.Count - itemCountTarget; i++)
            toDeleteHash.Add(decimateValues[i].Index);

        var result = new List<Vector2>();
        for(var i = 0; i < loop.Count; i++)
        {
            if (!toDeleteHash.Contains(i))
                result.Add(loop[i]);
        }

        return result.ToArray();
    }
    
    private int GetLoopedIndex(int index, int itemCount)
    {
        if (index < 0)
            return itemCount + index;

        if (index >= itemCount)
            return index % itemCount;
            
        return index;
    }

    
    private void TriangulateMeshLoops()
    {
        var h =  _worldHeight * (float)(_clusterMetadata.MaxValue / 255f) / (_horizontalLoops.Count - 1);
        var meshPointIndexCarrie = 0;
        
        for (var vI = 0; vI < _horizontalLoops.Count - 1; vI++)
        {
            var bottomPivotIndex = 0;
            var bottomPivotStretch = 0f;
            
            var upPivotIndex = 0;
            var upPivotStretch = 0f;
            
            while (true)
            {
                if (_killThreadFlag)
                {
                    _killThreadFlag = false;
                    return;
                }

                var bottomLoop = _horizontalLoops[vI];
                var upLoop = _horizontalLoops[vI + 1];

                var bottomStretchDelta = 0f;
                var upStretchDelta = 0f;
                
                try
                {
                    bottomStretchDelta =
                        (bottomLoop[bottomPivotIndex] - bottomLoop[(bottomPivotIndex + 1) % bottomLoop.Length])
                        .magnitude;

                    upStretchDelta = (upLoop[upPivotIndex] - upLoop[(upPivotIndex + 1) % upLoop.Length]).magnitude;
                }
                catch (IndexOutOfRangeException e)
                {
                    
                }

                var points = new Vector2[]
                {
                    Vector2.right * bottomPivotStretch * 5,
                    new Vector2(bottomPivotStretch + bottomStretchDelta, 0) * 5,
                    new Vector2(upPivotStretch, h) * 5,
                    new Vector2(upPivotStretch + upStretchDelta, h) * 5
                };

                //check https://en.wikipedia.org/wiki/Delaunay_triangulation to view this definition
                var alpha = Vector2.Angle(points[1] - points[0], points[2] - points[0]);
                var gamma = Vector2.Angle(points[1] - points[3], points[2] - points[3]);

                var oneTwoConnect =
                    alpha + gamma <= 180f; //should create a connection between point index 1 and 2 or between point index 0 and 3

                if (upPivotIndex == upLoop.Length)
                    oneTwoConnect = true;
                
                if (bottomPivotIndex == bottomLoop.Length)
                    oneTwoConnect = false;
                
                var meshIndexes = new int[3];

                if (oneTwoConnect)
                {
                    meshIndexes[0] = GetLoopedIndex(bottomPivotIndex + 1, bottomLoop.Length) + meshPointIndexCarrie;
                    meshIndexes[1] = GetLoopedIndex(upPivotIndex, upLoop.Length) + bottomLoop.Length + meshPointIndexCarrie;
                    meshIndexes[2] = GetLoopedIndex(bottomPivotIndex, bottomLoop.Length) + meshPointIndexCarrie;

                    if (bottomPivotIndex < bottomLoop.Length)
                    {
                        bottomPivotIndex++;
                        bottomPivotStretch += bottomStretchDelta;
                    }
                }
                else
                {
                    meshIndexes[0] = GetLoopedIndex(bottomPivotIndex, bottomLoop.Length) + meshPointIndexCarrie;
                    meshIndexes[1] = GetLoopedIndex(upPivotIndex + 1, upLoop.Length) + bottomLoop.Length + meshPointIndexCarrie;
                    meshIndexes[2] = GetLoopedIndex(upPivotIndex, upLoop.Length) + bottomLoop.Length + meshPointIndexCarrie;

                    if (upPivotIndex < upLoop.Length)
                    {
                        upPivotIndex++;
                        upPivotStretch += upStretchDelta;
                    }
                }
                
                foreach (var meshIndex in meshIndexes)
                    _triangles.Add(meshIndex);

                if (upPivotIndex >= upLoop.Length &&
                    bottomPivotIndex >= bottomLoop.Length) //got to the end of any loop
                {
                    meshPointIndexCarrie += bottomLoop.Length;
                    _updateMeshFlag = true;
                    Thread.Sleep(5000);
                    break;
                }
                
                Thread.Sleep(50);
            }
        }

        int GetLoopedIndex(int index, int listCount)
        {
            return index % listCount;
        }
    }

    public void KillThread()
    {
        _killThreadFlag = true;
    }
    
    public void MeshingTick()
    {
        if (_updateMeshFlag)
        {
            _mesh.vertices = _meshPoints.ToArray();
            _mesh.triangles = _triangles.ToArray();
            _mesh.RecalculateNormals();
            _updateMeshFlag = false;
        }
    }
    
    public void DrawGizmos()
    {
        //Gizmos.matrix = Matrix4x4.identity;
        
        Gizmos.color = new Color(_clusterMetadata.ClusterId / 50f, 0f, 1f);
        try
        {
            
            if (_meshPoints == null)
                return;

            for (var i = 0; i < _meshPoints.Length; i++)
                Gizmos.DrawSphere(_meshPoints[i], 0.005f);

            Gizmos.color = Color.yellow;
            if (_mesh != null)
            {
                Vector3 pa;
                Vector3 pb;
                
                for (var i = 0; i < _triangles.Count; i += 3)
                {
                    for (var j = 0; j < 3; j++)
                    {
                        pa = _meshPoints[_triangles[i + j]];
                        pb = _meshPoints[_triangles[i + (j + 1) % 3]];
                        Gizmos.DrawLine(pa, pb);
                    }
                }
            }
        }
        catch (Exception e)
        {
            
        }
        //gizmos stuffs
    }
    
    private class IndexAndDecimateValue : IComparable<IndexAndDecimateValue>
    {
        public int Index;
        public float DecimateValue;

        public int CompareTo(IndexAndDecimateValue other)
        {
            return DecimateValue.CompareTo(other.DecimateValue);
        }
    }
}
