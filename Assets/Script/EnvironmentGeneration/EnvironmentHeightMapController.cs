using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;


public class EnvironmentHeightMapController : BaseCameraBaker
{
    [SerializeField] private ComputeShader _imageProcessingComputeShader;
    [SerializeField] private Color _volumeColor;

    private Vector3 _originPosition;
    private Quaternion _originRotation;
    private Vector2 _horizontalArea;

    
    private bool _isProcessingTexture = false;
    private bool _clusterActionCallFlag = false;
    private Thread _clusterProcessingThread;
    private byte[] _processingTextureData;
    private int2 _processingTextureSize;
    private const byte CLUSTER_THRESHOLD = 5;
    private event Action OnMeshClusterFinishedAction;
    
    private List<LoopedLandMesh> _landMeshes;
    private List<ClusterMetaData> _clustersMetadata;
    
    private void Awake()
    {
        _bakeCamera.enabled = false;
        _clusterProcessingThread = new Thread(ComputeClustersProcess);
        
        OnMeshClusterFinishedAction += UpdateLoopedMeshes;
        //Initialize(Vector2.one * 200, 2, 0.01f, Vector3.zero, quaternion.identity);
    }
    
    public override void Initialize(Vector2 bakeArea, float texturePPU, float worldScale, Vector3 centerWorldPosition, Quaternion centerWorldRotation)
    {
        base.Initialize(bakeArea, texturePPU, worldScale, centerWorldPosition, centerWorldRotation);
        _originPosition = centerWorldPosition;
        _originRotation = centerWorldRotation;
        _horizontalArea = worldScale * bakeArea;

        BeginEnvironmentGeneration();
    }

    private async void BeginEnvironmentGeneration()
    {
        await RenderDepthMap(_bakeTexture);
        var scaledTexture = ResizeRenderTexture(_bakeTexture, new Vector2Int(80, 80));
        await Task.Delay(3000);
        PrepareTextureForClustering(scaledTexture);
    }
    
    private async Task RenderDepthMap(RenderTexture outputTexture)
    {
        //configure camera
        _bakeCamera.enabled = false;
        _bakeCamera.transform.position = _originPosition + _originRotation * (Vector3.up * _cameraDepth);
        _bakeCamera.nearClipPlane = 0.0f;
        _bakeCamera.farClipPlane = _cameraDepth;

        var cameraBufferRenderTexture = CloneRenderTextureWithProperties(outputTexture, true, RenderTextureFormat.Default);
        _bakeCamera.targetTexture = cameraBufferRenderTexture;

        var material = _bakeDebugMeshRenderer.material;
        material.SetTexture("_BaseMap", outputTexture);
        await Task.Yield();

        ClearTexture(outputTexture);
        ClearTexture(cameraBufferRenderTexture);

        await Task.Yield();

        var bakeKernel = _imageProcessingComputeShader.FindKernel("BakeHeightCS");

        if (!ComputeShaderUtilities.CheckComputeShaderTextureSize(
            _imageProcessingComputeShader,
            bakeKernel,
            cameraBufferRenderTexture.width,
            cameraBufferRenderTexture.height))
        {
            return;
        }

        if (!ComputeShaderUtilities.CheckComputeShaderTextureSize(
            _imageProcessingComputeShader,
            bakeKernel,
            outputTexture.width,
            outputTexture.height))
        {
            return;
        }

        var step = _cameraDepth / 255.0f;
        _imageProcessingComputeShader.SetTexture(bakeKernel, "InputTexture", cameraBufferRenderTexture);
        _imageProcessingComputeShader.SetTexture(bakeKernel, "ResultTexture", outputTexture);
        _imageProcessingComputeShader.SetInt("MaskChannel", 0); //write to the red channel
        
        for (var sliceInt = 254; sliceInt >= 0; sliceInt--)
        {
            _bakeCamera.nearClipPlane = sliceInt * step;
            _bakeCamera.farClipPlane = (sliceInt + 1) * step;
            _bakeCamera.Render();


            _imageProcessingComputeShader.SetFloat("NormalizedBakeHeight", 1f - (float)sliceInt / 255f);
            _imageProcessingComputeShader.Dispatch(
                bakeKernel,
                outputTexture.width / 4,
                outputTexture.height / 4,
                1);
        }
        
        await Task.Yield();
        cameraBufferRenderTexture.Release();
        Destroy(cameraBufferRenderTexture);
    }
    
    
    private void Update()
    {
        if (!_isProcessingTexture && _clusterActionCallFlag)
        {
            _clusterActionCallFlag = false;
            OnMeshClusterFinishedAction?.Invoke();
        }
        
        if (_landMeshes == null)
            return;

        foreach (var mesh in _landMeshes)
        {
            mesh.DrawLoops();   
        }
    }
    
    private void UpdateLoopedMeshes()
    {
        var t = new Texture2D(_processingTextureSize.x, _processingTextureSize.y, TextureFormat.RGBA32, false);
        /*
        for (var i = 0; i < _processingTextureData.Length; i += 4)
        {
            _processingTextureData[i] = 0;
            _processingTextureData[i + 2] = 0;
        }
        */
        
        t.filterMode = FilterMode.Point;
        t.SetPixelData(_processingTextureData, 0);
        t.Apply();
        
        
        var material = _bakeDebugMeshRenderer.material;
        material.SetTexture("_BaseMap", t);

        foreach (var cm in _clustersMetadata)
        {
            
        }
    }
    
#region CLUSTER_PROCESS
    private void PrepareTextureForClustering(RenderTexture source)
    {
        var kernel = _imageProcessingComputeShader.FindKernel("ExpandMaskCS");

        if (!ComputeShaderUtilities.CheckComputeShaderTextureSize(
            _imageProcessingComputeShader,
            kernel,
            source.width,
            source.height))
        {
            source.Release();
            Destroy(source);
            return;
        }
        
        _imageProcessingComputeShader.SetTexture(kernel, "ResultTexture", source);
        _imageProcessingComputeShader.SetInt("MaskChannel", 0);

        _imageProcessingComputeShader.Dispatch(
            kernel, 
            source.width / 4, 
            source.height / 4, 
            1);

        AsyncGPUReadback.Request(
            source,
            0,
            (req) =>
            {
                _processingTextureData = req.GetData<byte>().ToArray(); //rgba32 format
                _processingTextureSize = new int2(source.width, source.height);
                _clusterProcessingThread.Start();
            });
    }

    /// <summary>
    /// Computes the clusters from an image rgba32
    /// Uses the red channel as values for the cluster generation
    /// Uses the blue channel for storing cluster id
    /// Uses the green channel for storing visited pixels
    /// </summary>
    private void ComputeClustersProcess()
    {
        _isProcessingTexture = true;
        _clusterActionCallFlag = true; //only main thread can disable this
        
        var clusterQueue = new Queue<int2>();
        var nonVisitedFound = SearchForNonVisitedPixels(out var nonVisitedPoint);
        if (!nonVisitedFound)//handle nothing found
            return;

        clusterQueue.Enqueue(nonVisitedPoint);
        
        var searchDirections = new int2[]
        {
            new int2(-1, 0),
            new int2(0, 1),
            new int2(1, 0),
            new int2(0, -1)
        };

        var clusterMetadataList = new List<ClusterMetaData>();
        int currentClusterId = 5;
        
        int2 clusterRectBegin = _processingTextureSize;
        int2 clusterRectEnd = int2.zero;
        byte clusterMinValue = byte.MaxValue;
        byte clusterMaxValue = 0;
        
        while (true)
        {
            if (clusterQueue.Count == 0)
            {   
                clusterMetadataList.Add(new ClusterMetaData
                {
                    ClusterId = (byte)currentClusterId,
                    MinValue = clusterMinValue,
                    MaxValue = clusterMaxValue,
                    Bounds = new Rect(
                        (float)clusterRectBegin.x / _processingTextureSize.x, 
                        (float)clusterRectBegin.y / _processingTextureSize.y,
                        (float)(clusterRectEnd.x - clusterRectBegin.x) / _processingTextureSize.x,
                        (float)(clusterRectEnd.y - clusterRectBegin.y) / _processingTextureSize.y)
                });
                
                
                nonVisitedFound = SearchForNonVisitedPixels(out nonVisitedPoint);
                if(!nonVisitedFound)
                    break;

                clusterQueue.Enqueue(nonVisitedPoint);
                clusterRectBegin = _processingTextureSize;
                clusterRectEnd = int2.zero;
                clusterMinValue = byte.MaxValue;
                clusterMaxValue = 0;
                
                currentClusterId += 5;
            }
            
            var currentPosition = clusterQueue.Dequeue();
            IsPixelInRange(currentPosition, out var currentIndex);
            
            if(_processingTextureData[currentIndex + 2] != 0) //don't process if it has passed
                continue;
            
            var currentPixel = GetPixelValues(currentIndex);
            foreach (var sd in searchDirections)
            {
                var neighborPos = currentPosition + sd;
                
                if(!IsPixelInRange(neighborPos, out var neighborIndex))
                    continue;

                var neighborValues = GetPixelValues(neighborIndex);
                var dif = Mathf.Abs(currentPixel[0] - neighborValues[0]);
                if (dif <= CLUSTER_THRESHOLD && neighborValues[3] != 0)
                {
                    clusterQueue.Enqueue(neighborPos);
                }
            }

            _processingTextureData[currentIndex + 1] = (byte)currentClusterId;
            _processingTextureData[currentIndex + 2] = byte.MaxValue;
            UpdateClusterMetadataInfo(currentPosition, currentPixel[0]);
        }

        MergeClusters(clusterMetadataList, 3f / _processingTextureSize.x);
        _clustersMetadata = clusterMetadataList;
        _isProcessingTexture = false;
        
        bool SearchForNonVisitedPixels(out int2 result)
        {
            for (var y = 0; y < _processingTextureSize.y; y++)
            {
                for (var x = 0; x < _processingTextureSize.x; x++)
                { 
                    result = new int2(x, y);
                    IsPixelInRange(result, out var index);
                    var pixelValues = GetPixelValues(index);
                    if (pixelValues[2] == 0 && pixelValues[3] != 0)
                        return true;
                }
            }

            result = int2.zero;
            return false;
        }
        
        bool IsPixelInRange(int2 pos, out int index)
        {
            if (pos.x < 0 || pos.x >= _processingTextureSize.x ||
                pos.y < 0 || pos.y >= _processingTextureSize.y)
            {
                index = -1;
                return false;
            }
            
            index = (pos.x + pos.y * _processingTextureSize.x) * 4;
            var outOfScope = index < 0 || index >= _processingTextureData.Length;
            return !outOfScope;
        }

        byte[] GetPixelValues(int index)
        {
            try
            {
                var values = new byte[4];
                for (var i = 0; i < 4; i++)
                    values[i] = _processingTextureData[index + i];
                return values;
            }
            catch (Exception e)
            {
                Debug.LogError($"Error getting index {index} from  values {_processingTextureData.Length}");
                return new byte[0];
            }
        }

        void UpdateClusterMetadataInfo(int2 pos, byte value)
        {
            if (clusterRectBegin.x > pos.x)
                clusterRectBegin.x = pos.x;
            
            if (clusterRectBegin.y > pos.y)
                clusterRectBegin.y = pos.y;

            pos += new int2(1, 1);
            if (clusterRectEnd.x < pos.x)
                clusterRectEnd.x = pos.x;
            
            if (clusterRectEnd.y < pos.y)
                clusterRectEnd.y = pos.y;

            if (clusterMinValue > value)
                clusterMinValue = value;

            if (clusterMaxValue < value)
                clusterMaxValue = value;

        }
    }

    private void MergeClusters(List<ClusterMetaData> clustersMetadata, float mergeThreshold)
    {
        var toMergeList = new List<ClusterMetaData>();
        for (var i = 0; i < clustersMetadata.Count; i++)
        {
            var clusterMeta = clustersMetadata[i];
            if (clusterMeta.Bounds.size.x <= mergeThreshold || clusterMeta.Bounds.size.y <= mergeThreshold)
            {
                clustersMetadata.RemoveAt(i);
                toMergeList.Add(clusterMeta);
                i--;
            }
        }

        for (var i = 0; i < clustersMetadata.Count; i++)
        {
            var mergeCandidate = clustersMetadata[i];
            for (var j = 0; j < toMergeList.Count; j++)
            {
                var toMergeBound = toMergeList[j];
                var points = new Vector2[]
                {
                    toMergeBound.Bounds.position,
                    toMergeBound.Bounds.position + toMergeBound.Bounds.width * Vector2.right,
                    toMergeBound.Bounds.position + toMergeBound.Bounds.height * Vector2.up,
                    toMergeBound.Bounds.position + new Vector2(toMergeBound.Bounds.width, toMergeBound.Bounds.height),
                };

                var containsAll = true;
                foreach (var p in points)
                {
                    if (!mergeCandidate.Bounds.Contains(p))
                    {
                        containsAll = false;
                        break;
                    }   
                }

                if (containsAll)
                {
                    //do merge 
                    toMergeList.RemoveAt(j);
                    if (mergeCandidate.MinValue > toMergeBound.MinValue)
                        mergeCandidate.MinValue = toMergeBound.MinValue;
                    
                    if (mergeCandidate.MaxValue < toMergeBound.MaxValue)
                        mergeCandidate.MaxValue = toMergeBound.MaxValue;

                    clustersMetadata[i] = mergeCandidate;
                    break;
                }
            }
        }
    }
    
#endregion

#region UTILITIES

    /// <summary>
    /// Creates a new texture with the same size as the rt parameter
    /// Will NOT copy content
    /// can change the format
    /// </summary>
    /// <param name="rt"></param>
    /// <param name="enableRandomWrite"></param>
    /// <param name="cloneFormat"></param>
    /// <returns></returns>
    private RenderTexture CloneRenderTextureWithProperties(RenderTexture rt, bool enableRandomWrite, RenderTextureFormat cloneFormat)
    {
        RenderTexture clone;
        clone = new RenderTexture(rt.width, rt.height, 0, cloneFormat);
        clone.filterMode = rt.filterMode;
        clone.enableRandomWrite = enableRandomWrite;
        clone.Create();
        return clone;
    }

    private void ClearTexture(RenderTexture targetTexture)
    {
        var bakeKernel = _imageProcessingComputeShader.FindKernel("ClearBakeCS");

        if (!ComputeShaderUtilities.CheckComputeShaderTextureSize(
            _imageProcessingComputeShader,
            bakeKernel,
            targetTexture.width,
            targetTexture.height))
        {
            return;
        }


        _imageProcessingComputeShader.SetFloat("NormalizedBakeHeight", 0.1f);
        _imageProcessingComputeShader.SetTexture(bakeKernel, "ResultTexture", targetTexture);

        _imageProcessingComputeShader.Dispatch(
            bakeKernel,
            targetTexture.width / 4,
            targetTexture.height / 4,
            1);
    }

    private RenderTexture ResizeRenderTexture(RenderTexture source, Vector2Int newSize, bool debugTexture = true, bool destroySource = true)
    {
        var scaledRt = new RenderTexture(newSize.x, newSize.y, 0, source.format);
        scaledRt.filterMode = FilterMode.Point;
        scaledRt.enableRandomWrite = true;
        Graphics.Blit(source, scaledRt);

        if (destroySource)
        {
            source.Release();
            Destroy(source);
        }

        if (debugTexture)
        {
            var material = _bakeDebugMeshRenderer.material;
            material.SetTexture("_BaseMap", scaledRt);
        }
        
        return scaledRt;
    }
    
#endregion
    
    private void OnDrawGizmos()
    {
        if (!_isInitialized)
            return;
        Gizmos.color = _volumeColor;
        Gizmos.matrix = Matrix4x4.TRS(_originPosition, _originRotation, Vector3.one);
        Gizmos.DrawWireCube(Vector3.up * 0.5f * _cameraDepth, new Vector3(_horizontalArea.x, _cameraDepth, _horizontalArea.y));

        if (_clustersMetadata == null)
            return;
        
        Gizmos.matrix *= Matrix4x4.Scale(new Vector3(_horizontalArea.x, 1, _horizontalArea.y));
        Gizmos.matrix *= Matrix4x4.Translate((Vector3.left + Vector3.forward) * -0.5f);
        
        Gizmos.color = Color.yellow;
        foreach (var clusterMeta in _clustersMetadata)
        {
            var center = clusterMeta.Bounds.center;
            var distance = clusterMeta.Bounds.size;

            var minHeight = ((float)clusterMeta.MinValue / byte.MaxValue) * _cameraDepth;
            var maxHeight = ((float)clusterMeta.MaxValue / byte.MaxValue) * _cameraDepth;
            
            Gizmos.DrawWireCube(
                new Vector3(center.x,   maxHeight * 0.5f, center.y), 
                new Vector3(distance.x, maxHeight, distance.y));
        }
    }
}

public struct ClusterMetaData
{
    public byte ClusterId;
    public byte MinValue;
    public byte MaxValue;
    public Rect Bounds;
}
