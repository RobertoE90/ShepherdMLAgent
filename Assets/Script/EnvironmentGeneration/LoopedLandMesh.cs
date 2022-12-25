using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Unity.Entities.UniversalDelegates;
using Unity.Mathematics;
using UnityEngine;

public class LoopedLandMesh
{
    private Transform _parentTransform;
    private Thread _meshingThread;
    
    private byte[] _textureData;
    private int2 _texturePixelSize;
    private Vector2 _worldSpaceArea;

    private ClusterMetaData _clusterMetadata;
    private List<Vector2[]> _horizontalLoops;

    private int2[] _skeletonizeSearchPath =
    {
        int2.zero,
        new int2(0, 1),
        new int2(1, 1),
        new int2(1, 0),
        new int2(1, -1),
        new int2(0, -1),
        new int2(-1, -1),
        new int2(-1, 0),
        new int2(-1, 1)
    };

    
    public LoopedLandMesh(ClusterMetaData metadata, int2 textureSize, Vector2 worldSpaceArea, Transform parent)
    {
        _parentTransform = parent;
        _texturePixelSize = textureSize;
        _worldSpaceArea = worldSpaceArea;
        _clusterMetadata = metadata;
        
        _horizontalLoops = new List<Vector2[]>();
        _meshingThread = new Thread(ComputeHorizontalLoopProcess);
    }

    public void UpdateTextureData(byte[] textureData)
    {
        _textureData = textureData;
        //_meshingThread.Start();
        
        inspectPoint = 0;
        _edges = _clusterMetadata.EdgePoints.ToArray();
        toDeleteList = new List<int2>();
        wak = true;
    }

    private bool wak = false;
    
    private void ComputeHorizontalLoopProcess()
    {
        
        //var horizontalLoop = ComputeHorizontalLoop();
        //Thread.Yield();
        //_horizontalLoops.Add(horizontalLoop.ToArray());
        
        //var decimatedLoop = DecimateLoop(horizontalLoop, 30);
        //_horizontalLoops.Add(decimatedLoop);
        Thread.Yield();
        //wak = true;
    }

    public void DrawLoops()
    {
        if (!wak)
            return;
        
        ComputeHorizontalLoopTick();
        
        /*
        foreach (var hl in _horizontalLoops)
        {
            for (var i = 0; i < hl.Length; i++)
            {
                var pa = hl[i];
                var pb = hl[(i + 1) % hl.Length];
                Debug.DrawLine(new Vector3(pa.x, 0, pa.y), new Vector3(pb.x, 0, pb.y), Color.green);
            }
        }
        */

        
    }

    public void DrawEdges()
    {
        Gizmos.matrix = Matrix4x4.identity;
        Gizmos.color = Color.red;
        foreach (var ep in _clusterMetadata.EdgePoints)
        {
            Gizmos.DrawCube(new Vector3(ep.x, 0, ep.y), Vector3.one);
        }
    }
    
    #region CONCAVE_HULL_METHOD

    private int inspectPoint;
    private int2[] _edges;
    private List<int2> toDeleteList;
    private bool _removeEcxeed = false;
    
    private void ComputeHorizontalLoopTick()
    {
        if (inspectPoint >= _edges.Length)
        {
            inspectPoint = 0;
            foreach (var toDelete in toDeleteList)
                _clusterMetadata.EdgePoints.Remove(toDelete);

            if(!_removeEcxeed)
                _removeEcxeed = toDeleteList.Count == 0;
            
            Debug.Log($"killed {_removeEcxeed} : {toDeleteList.Count} ");
            toDeleteList.Clear();
            
            _edges = _clusterMetadata.EdgePoints.ToArray();
            Debug.Break();
        }
        
        if(_edges.Length == 0)
            return;

        var searchPoint = _edges[inspectPoint];
        if (!_clusterMetadata.EdgePoints.Contains(searchPoint))
        {
            inspectPoint++;
            return;
        }

        if (_removeEcxeed)
        {
            var sum = 0;
            for (var y = -1; y <= 1; y++)
            {
                for (var x = -1; x <= 1; x++)
                {
                    if (_clusterMetadata.EdgePoints.Contains(searchPoint + new int2(x, y)))
                        sum++;
                }
            }

            if (sum != 3)
            {
                toDeleteList.Add(searchPoint);
                Debug.Log($"for this {sum}");
                Debug.DrawRay(new Vector3(searchPoint.x, 0, searchPoint.y), Vector3.up * 5, Color.blue);
                Debug.Break();
            }

            inspectPoint++;
            return;
        }

        var sumCenter = 0;
        var sumTransitions = 0;
        
        var condC = false;
        var condD = false;
        
        var condE = false;
        var condF = false;

        
        for (var i = 0; i < _skeletonizeSearchPath.Length; i++)
        {
            var candidatePoint = searchPoint + _skeletonizeSearchPath[i];
            var isCandidatePositive = _clusterMetadata.EdgePoints.Contains(candidatePoint);
            if (isCandidatePositive)
                sumCenter++;

            if (i == 1 || i == 3 || i == 5)
                condC |= isCandidatePositive;
            
            if (i == 3 || i == 5 || i == 7)
                condD |= isCandidatePositive;

            if (i == 1 || i == 3 || i == 7)
                condE |= isCandidatePositive;
            
            if (i == 1 || i == 5 || i == 7)
                condF |= isCandidatePositive;
            
            if(i == 0)
                continue;

            var nextInLoop = i + 1;
            if (nextInLoop == _skeletonizeSearchPath.Length)
                nextInLoop = 1;
            
            var isNextCandidatePositive = _clusterMetadata.EdgePoints.Contains(searchPoint + _skeletonizeSearchPath[nextInLoop]);

            if (!isCandidatePositive && isNextCandidatePositive)
                sumTransitions++;
        }

        var condA = 2 <= sumCenter && sumCenter <= 6;
        var condB = sumTransitions == 1;

        condC = !condC && !condD;
        condE = !condE && !condF;
        
        if (condA && condB)
        {
            if (condC || condE)
                toDeleteList.Add(searchPoint);
        }
        
        Debug.DrawRay(new Vector3(searchPoint.x, 0, searchPoint.y), Vector3.up * 5, Color.blue);
        //Debug.Log($"waka {condC} : {condD}");
        //Debug.Break();

        inspectPoint++;
    }

    
    
    #endregion

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

        for (var i = 0; i < loop.Count; i++)
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
