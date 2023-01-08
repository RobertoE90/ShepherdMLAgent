using System;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;



public class SheepDotsManager : MonoBehaviour
{
    [Header("Herd Config")]
    [SerializeField] private int _sheepCount;
    [SerializeField] private int _updateGroupCount = 100;
    [Space(20)]
    [SerializeField] private float _spawnSquareSide;
    [SerializeField] private float _globalBakeTexturesPPU = 2f;
    [SerializeField] private float _worldScale;
    public float WorldScale => _worldScale;

    [Space(20)]
    [SerializeField] Mesh _sheepMesh;
    [SerializeField] Material _sheepMaterial;


    [Header("External references")]
    [SerializeField] private BaseCameraBaker[] _cameraBakers;
    [SerializeField] private BaseEntityCameraBaker[] _cameraEntityBakers;

    private NativeArray<Entity> _sheepEntities;
    private EntityManager _entityManager;

    private Entity _globalParamsEntity;
    private const int RANDOM_VALUES_COUNT = 10;

    private void Awake()
    {
        _entityManager = World.DefaultGameObjectInjectionWorld.EntityManager;
        SpawnGlobalParamsEntity();
        SpawnHerd();

        foreach (var baker in _cameraBakers)
            baker.Initialize(Vector2.one * _spawnSquareSide, _globalBakeTexturesPPU, _worldScale, transform.position, transform.rotation);

        foreach (var baker in _cameraEntityBakers)
            baker.Initialize(_sheepCount, Vector2.one * _spawnSquareSide, _globalBakeTexturesPPU, _worldScale, transform.position, transform.rotation);
       
        var entitiesInputManager = InputEntityManager.Instance;
        if (entitiesInputManager != null)
        {
            entitiesInputManager.SetInputReferenceMatrix(transform.localToWorldMatrix);
            entitiesInputManager.Initialize(Vector2.one * _spawnSquareSide, _worldScale, _globalBakeTexturesPPU);
        }
    }

    private void SpawnGlobalParamsEntity()
    {
        _globalParamsEntity = _entityManager.CreateEntity(new ComponentType[]{
            typeof(GlobalParams),
        });
        _entityManager.SetComponentData<GlobalParams>(_globalParamsEntity, new GlobalParams
        {
            MaxGroups = _updateGroupCount,
            WorldScale = _worldScale
        });

        _entityManager.AddBuffer<RandomData>(_globalParamsEntity);
    }

    private void Update()
    {
        var dynamicBuffer = _entityManager.GetBuffer<RandomData>(_globalParamsEntity);
        if (dynamicBuffer.Length != RANDOM_VALUES_COUNT)
        {
            for (var i = dynamicBuffer.Length; i < RANDOM_VALUES_COUNT; i++)
            {
                dynamicBuffer.Add(new RandomData
                {
                    Value = UnityEngine.Random.value
                });
            }
            return;
        }
        var inputBuffer = dynamicBuffer.Reinterpret<float>();

        for (var i = 0; i < inputBuffer.Length; i++)
            inputBuffer[i] = UnityEngine.Random.value;
    }


    private async void SpawnHerd()
    {
        await Task.Run(() => { });

        var archetype = _entityManager.CreateArchetype(new ComponentType[] { 
            typeof(LocalToWorld),
            typeof(Translation), 
            typeof(Rotation),
            typeof(NonUniformScale),
            typeof(WorldRenderBounds),
            typeof(RenderBounds),
            typeof(ChunkWorldRenderBounds),
            typeof(PerInstanceCullingTag),
            typeof(RenderMesh),
            typeof(SheepComponentDataEntity),

        });

        _sheepEntities = new NativeArray<Entity>(_sheepCount, Allocator.Persistent);
        _entityManager.CreateEntity(archetype, _sheepEntities);

        var meshComponent = new RenderMesh
        {
            material = _sheepMaterial,
            mesh = _sheepMesh,
        };

        var sheepBounds = new AABB {
            Center = new float3(0f, 0.5f, 0f),
            Extents = new float3(0.6f, 1f, 1f)
        };

        for (var i = 0; i < _sheepEntities.Length; i++)
        {
            _entityManager.SetSharedComponentData<RenderMesh>(_sheepEntities[i], meshComponent);
            _entityManager.SetComponentData<NonUniformScale>(_sheepEntities[i], new NonUniformScale { Value = Vector3.one * _worldScale });
            _entityManager.SetComponentData<Rotation>(_sheepEntities[i], new Rotation { Value = Quaternion.identity });
            _entityManager.SetComponentData<Translation>(
                _sheepEntities[i],
                new Translation
                {
                    Value = new Vector3(UnityEngine.Random.value - 0.5f, 0, UnityEngine.Random.value - 0.5f) * _spawnSquareSide * _worldScale
                });

            _entityManager.SetComponentData<SheepComponentDataEntity>(
                _sheepEntities[i],
                new SheepComponentDataEntity
                {
                    InputAttrackIndex = UnityEngine.Random.Range(0, InputEntityManager.Instance.InputAttractCount),
                    UpdateGroupId = (i % _updateGroupCount),
                    CurrentState = UnityEngine.Random.Range(0, 4)
                });;

            _entityManager.SetComponentData<RenderBounds>(_sheepEntities[i], new RenderBounds { Value = sheepBounds });
        }
    }

    private void OnDestroy()
    {
        try
        {
            _sheepEntities.Dispose();
        }catch(ObjectDisposedException e){}
    }
}
