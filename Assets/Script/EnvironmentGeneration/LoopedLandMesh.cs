using System;
using System.Collections.Generic;
using System.Threading;
using Unity.Mathematics;
using UnityEngine;

public class LoopedLandMesh
{
    private Thread _meshingThread;
    
    //process info
    private byte[] _textureData;
    private Rect _region;
    private Vector2Int _texturePixelSize;
    private Vector2 _worldSpaceArea;

    public LoopedLandMesh()
    {
        _meshingThread = new Thread(ComputeHorizontalLoopProcess);
    }

    public void UpdateProcessInfo(byte[] textureData, Rect processRegion, Vector2Int textureSize, Vector2 worldSpaceArea)
    {
        _textureData = textureData;
        _region = processRegion;
        _texturePixelSize = textureSize;
        _worldSpaceArea = worldSpaceArea;

        _meshingThread.Start();
    }

    private void ComputeHorizontalLoopProcess()
    {
        var horizontalLoop = ComputeHorizontalLoop();
        Thread.Yield();
        horizontalLoop = DecimateLoop(horizontalLoop, 30);

        Thread.Yield();
        for (var i = 0; i < horizontalLoop.Count; i++)
            Debug.DrawLine(horizontalLoop[i] * 5, horizontalLoop[(i + 1) % horizontalLoop.Count] * 5, Color.green, 100);

        Debug.Log("done");
    }
    
    private List<Vector3> ComputeHorizontalLoop()
    {
        var imageCoordNormalizer = new Vector2(1f / _texturePixelSize.x, 1f / _texturePixelSize.y);

        var points = new List<Vector2>();
        var pointsToIndexDic = new Dictionary<Vector2Int, int>();
        var edgesDic = new Dictionary<int, int2>();

        for (var y = -1; y <= _texturePixelSize.y; y++)
        {
            for (var x = -1; x <= _texturePixelSize.x; x++)
            {
                var searchPos = new Vector2Int(x, y);
                var meshSheetIndex = GetMaskFromSquare(_textureData, searchPos, Vector2Int.zero, _texturePixelSize);

                var meshSheetList = MeshingUtility.MarchingSquaresMeshSheedAt(meshSheetIndex);
                var indexConnectionList = new List<int>();
                for (var i = 0; i < meshSheetList.Length; i += 2)
                {
                    var pA = searchPos + MeshingUtility.MarchingSquaresSearchSheedAt(meshSheetList[i]);
                    var pB = searchPos + MeshingUtility.MarchingSquaresSearchSheedAt(meshSheetList[i + 1]);

                    var pointKey = new Vector2Int((int)(pA.x + pB.x), (int)(pA.y + pB.y));
                    if (!pointsToIndexDic.ContainsKey(pointKey))
                    {
                        pointsToIndexDic.Add(pointKey, points.Count);
                        points.Add(Vector2.Scale((pA + pB) * 0.5f, imageCoordNormalizer)); //the point dimensions are normalized to the image size
                    }

                    indexConnectionList.Add(pointsToIndexDic[pointKey]);
                }

                for (var i = 0; i < indexConnectionList.Count; i += 2)
                {
                    var edgePointA = indexConnectionList[i];
                    var edgePointB = indexConnectionList[i + 1];

                    if (!edgesDic.ContainsKey(edgePointA))
                    {
                        edgesDic.Add(edgePointA, new int2(edgePointB, -1));
                    }
                    else
                    {
                        var edge = edgesDic[edgePointA];
                        edge.y = edgePointB;
                        edgesDic[edgePointA] = edge;
                    }

                    if (!edgesDic.ContainsKey(edgePointB))
                    {
                        edgesDic.Add(edgePointB, new int2(edgePointA, -1));
                    }
                    else
                    {
                        var edge = edgesDic[edgePointB];
                        edge.y = edgePointA;
                        edgesDic[edgePointB] = edge;
                    }
                }
            }
        }

        var result = new List<Vector3>();
        var addedHash = new HashSet<int>();
        var currentPointIndex = 0;

        AddPointToResultList(points[currentPointIndex]);

        while (result.Count != points.Count)
        {
            if (!addedHash.Contains(edgesDic[currentPointIndex].x))
                currentPointIndex = edgesDic[currentPointIndex].x;
            else if (!addedHash.Contains(edgesDic[currentPointIndex].y))
                currentPointIndex = edgesDic[currentPointIndex].y;
            else
            {
                Debug.LogError("Both point edges are added");
                break;
            }

            AddPointToResultList(points[currentPointIndex]);
        }

        void AddPointToResultList(Vector2 point)
        {
            result.Add(new Vector3(point.x * _worldSpaceArea.x, 0, point.y * _worldSpaceArea.y));
            addedHash.Add(currentPointIndex);
        }

        return result;
    }

    private int GetMaskFromSquare(byte[] data, Vector2 squareZeroPos, Vector2Int imageRectOrigin, Vector2Int imageSize)
    {
        int mask = 0;
        for (var i = 0; i < MeshingUtility.GetMarchingSquaresSearchItemCount(); i++)
        {
            var searchPos = imageRectOrigin + squareZeroPos + MeshingUtility.MarchingSquaresSearchSheedAt(i);
            byte sample = SampleImageData(
                    data,
                    imageSize,
                    searchPos,
                    1);

            if (sample != 0)
                mask = mask | 1 << i;
        }

        return mask;
    }

    private byte SampleImageData(byte[] data, Vector2Int imageSize, Vector2 samplePoint, int imageChannels = 4, int channel = 0)
    {
        if (samplePoint.x < 0 || samplePoint.x >= imageSize.x ||
            samplePoint.y < 0 || samplePoint.y >= imageSize.y)
            return 0;

        var index = ((int)samplePoint.x + (int)samplePoint.y * (int)imageSize.x) * imageChannels + channel;

        if (index >= data.Length)
            return 0;

        return data[index];
    }

    private List<Vector3> DecimateLoop(List<Vector3> loop, int itemCountTarget)
    {
        if (itemCountTarget < 3)
        {
            Debug.LogWarning("Trying to decimate under a polygon");
            return loop;
        }

        if (loop.Count <= itemCountTarget)
        {
            Debug.LogWarning($"Trying to decimate to {itemCountTarget} with an array of {loop.Count}");
            return loop;
        }

        var decimateValues = new List<IndexAndDecimateValue>(loop.Count);

        for (var i = 0; i < loop.Count; i++)
        {
            var edgeA = loop[GetLoopedIndex(i, loop.Count)] - loop[GetLoopedIndex(i - 1, loop.Count)];
            var edgeB = loop[GetLoopedIndex(i, loop.Count)] - loop[GetLoopedIndex(i + 1, loop.Count)];

            decimateValues.Add(new IndexAndDecimateValue
            {
                Index = i,
                DecimateValue = Math.Abs(Vector3.Dot(edgeA, edgeB))
            });
        }
        decimateValues.Sort();
        
        var toDeleteHash = new HashSet<int>();
        for (var i = 0; i < loop.Count - itemCountTarget; i++)
            toDeleteHash.Add(decimateValues[i].Index);

        var result = new List<Vector3>();
        for(var i = 0; i < loop.Count; i++)
        {
            if (!toDeleteHash.Contains(i))
                result.Add(loop[i]);
        }

        return result;

        int GetLoopedIndex(int index, int itemCount)
        {
            if (index < 0)
                return itemCount + index;

            if (index >= itemCount)
                return index % itemCount;
            
            return index;
        }
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
