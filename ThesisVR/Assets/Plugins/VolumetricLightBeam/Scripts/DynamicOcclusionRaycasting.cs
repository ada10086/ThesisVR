﻿using UnityEngine;
using UnityEngine.Serialization;

namespace VLB
{
    [ExecuteInEditMode]
    [HelpURL(Consts.HelpUrlDynamicOcclusionRaycasting)]
    public class DynamicOcclusionRaycasting : DynamicOcclusionAbstractBase
    {
        /// <summary>
        /// Should it interact with 2D or 3D occluders?
        /// </summary>
        public OccluderDimensions dimensions = Consts.DynOcclusionRaycastingDimensionsDefault;

        /// <summary>
        /// The beam can only be occluded by objects located on the layers matching this mask.
        /// It's very important to set it as restrictive as possible (checking only the layers which are necessary)
        /// to perform a more efficient process in order to increase the performance.
        /// </summary>
        public LayerMask layerMask = Consts.DynOcclusionLayerMaskDefault;

        /// <summary>
        /// Should this beam be occluded by triggers or not?
        /// </summary>
        public bool considerTriggers = Consts.DynOcclusionRaycastingConsiderTriggersDefault;

        /// <summary>
        /// Minimum 'area' of the collider to become an occluder.
        /// Colliders smaller than this value will not block the beam.
        /// </summary>
        public float minOccluderArea = Consts.DynOcclusionRaycastingMinOccluderAreaDefault;

        /// <summary>
        /// Approximated percentage of the beam to collide with the surface in order to be considered as occluder
        /// </summary>
        public float minSurfaceRatio = Consts.DynOcclusionRaycastingMinSurfaceRatioDefault;

        /// <summary>
        /// Max angle (in degrees) between the beam and the surface in order to be considered as occluder
        /// </summary>
        public float maxSurfaceDot = Consts.DynOcclusionRaycastingMaxSurfaceDotDefault;

        /// <summary>
        /// Alignment of the computed clipping plane:
        /// </summary>
        public PlaneAlignment planeAlignment = Consts.DynOcclusionRaycastingPlaneAlignmentDefault;

        /// <summary>
        /// Translate the plane. We recommend to set a small positive offset in order to handle non-flat surface better.
        /// </summary>
        public float planeOffset = Consts.DynOcclusionRaycastingPlaneOffsetDefault;

        /// <summary>
        /// Fade out the beam before the computed clipping plane in order to soften the transition.
        /// </summary>
        [FormerlySerializedAs("fadeDistanceToPlane")]
        public float fadeDistanceToSurface = Consts.DynOcclusionFadeDistanceToSurfaceDefault;

        [System.Obsolete("Use 'fadeDistanceToSurface' instead")]
        public float fadeDistanceToPlane { get { return fadeDistanceToSurface; } set { fadeDistanceToSurface = value; } }


        public bool IsColliderHiddenByDynamicOccluder(Collider collider)
        {
            Debug.Assert(collider, "You should pass a valid Collider to VLB.DynamicOcclusion.IsColliderHiddenByDynamicOccluder");

            if (!planeEquationWS.IsValid())
                return false;

            var isInside = GeometryUtility.TestPlanesAABB(new Plane[] { planeEquationWS }, collider.bounds);
            return !isInside;
        }

        public class HitResult
        {
            public HitResult(RaycastHit hit3D)
            {
                point = hit3D.point;
                normal = hit3D.normal;
                distance = hit3D.distance;
                collider3D = hit3D.collider;
                collider2D = null;
            }

            public HitResult(RaycastHit2D hit2D)
            {
                point = hit2D.point;
                normal = hit2D.normal;
                distance = hit2D.distance;
                collider2D = hit2D.collider;
                collider3D = null;
            }

            public HitResult()
            {
                point = Vector3.zero;
                normal = Vector3.zero;
                distance = 0;
                collider2D = null;
                collider3D = null;
            }

            public Vector3 point;
            public Vector3 normal;
            public float distance;

            Collider2D collider2D;
            Collider collider3D;

            public bool hasCollider { get { return collider2D || collider3D; } }

            public string name
            {
                get
                {
                    if (collider3D) return collider3D.name;
                    else if (collider2D) return collider2D.name;
                    else return "null collider";
                }
            }

            public Bounds bounds
            {
                get
                {
                    if (collider3D) return collider3D.bounds;
                    else if (collider2D) return collider2D.bounds;
                    else return new Bounds();
                }
            }
        }

        /// <summary>
        /// Get information about the current occluder hit by the beam.
        /// Can be null if the beam is not occluded.
        /// </summary>
        public HitResult currentHit { get; private set; }

        protected override string GetShaderKeyword() { return "VLB_OCCLUSION_CLIPPING_PLANE"; }
        protected override MaterialManager.DynamicOcclusion GetDynamicOcclusionMode() { return MaterialManager.DynamicOcclusion.ClippingPlane; }

        float m_RangeMultiplier = 1f;
        Plane planeEquationWS { get; set; }

#if UNITY_EDITOR
        public struct EditorDebugData
        {
            public int lastFrameUpdate;
        }
        public EditorDebugData editorDebugData;

        public static bool editorShowDebugPlane = true;
        public static bool editorRaycastAtEachFrame = true;
        private static bool editorPrefsLoaded = false;

        public static void EditorLoadPrefs()
        {
            if (!editorPrefsLoaded)
            {
                editorShowDebugPlane = UnityEditor.EditorPrefs.GetBool("VLB_DYNOCCLUSION_SHOWDEBUGPLANE", true);
                editorRaycastAtEachFrame = UnityEditor.EditorPrefs.GetBool("VLB_DYNOCCLUSION_RAYCASTINEDITOR", true);
                editorPrefsLoaded = true;
            }
        }
#endif

        protected override void OnValidateProperties()
        {
            base.OnValidateProperties();
            minOccluderArea = Mathf.Max(minOccluderArea, 0f);
            fadeDistanceToSurface = Mathf.Max(fadeDistanceToSurface, 0f);
        }

        protected override void OnEnablePostValidate()
        {
            currentHit = null;

#if UNITY_EDITOR
            EditorLoadPrefs();
            editorDebugData.lastFrameUpdate = 0;
#endif
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            SetHit(null);
        }

        void Start()
        {
            if (Application.isPlaying)
            {
                var triggerZone = GetComponent<TriggerZone>();
                if (triggerZone)
                {
                    m_RangeMultiplier = Mathf.Max(1f, triggerZone.rangeMultiplier);
                }
            }
        }
        
        Vector3 GetRandomVectorAround(Vector3 direction, float angleDiff)
        {
            var halfAngle = angleDiff * 0.5f;
            return Quaternion.Euler(Random.Range(-halfAngle, halfAngle), Random.Range(-halfAngle, halfAngle), Random.Range(-halfAngle, halfAngle)) * direction;
        }

        QueryTriggerInteraction queryTriggerInteraction { get { return considerTriggers ? QueryTriggerInteraction.Collide : QueryTriggerInteraction.Ignore; } }

        float raycastMaxDistance { get { return m_Master.fallOffEnd * m_RangeMultiplier * transform.lossyScale.z; } }

        HitResult GetBestHit(Vector3 rayPos, Vector3 rayDir)
        {
            return dimensions == OccluderDimensions.Occluders2D ? GetBestHit2D(rayPos, rayDir) : GetBestHit3D(rayPos, rayDir);
        }

        HitResult GetBestHit3D(Vector3 rayPos, Vector3 rayDir)
        {
            var hits = Physics.RaycastAll(rayPos, rayDir, raycastMaxDistance, layerMask.value, queryTriggerInteraction);

            int bestHit = -1;
            float bestLength = float.MaxValue;
            for (int i = 0; i < hits.Length; ++i)
            {
                if (hits[i].collider.bounds.GetMaxArea2D() >= minOccluderArea)
                {
                    if (hits[i].distance < bestLength)
                    {
                        bestLength = hits[i].distance;
                        bestHit = i;
                    }
                }
            }

            if (bestHit != -1)
                return new HitResult(hits[bestHit]);
            else
                return new HitResult();
        }

        HitResult GetBestHit2D(Vector3 rayPos, Vector3 rayDir)
        {
            var hits = Physics2D.RaycastAll(new Vector2(rayPos.x, rayPos.y), new Vector2(rayDir.x, rayDir.y), raycastMaxDistance, layerMask.value);

            int bestHit = -1;
            float bestLength = float.MaxValue;
            for (int i = 0; i < hits.Length; ++i)
            {
                if (!considerTriggers && hits[i].collider.isTrigger) // do not query triggers if considerTriggers is disabled
                    continue;

                if (hits[i].collider.bounds.GetMaxArea2D() >= minOccluderArea)
                {
                    if (hits[i].distance < bestLength)
                    {
                        bestLength = hits[i].distance;
                        bestHit = i;
                    }
                }
            }

            if (bestHit != -1)
                return new HitResult(hits[bestHit]);
            else
                return new HitResult();
        }

        enum Direction {Up, Right, Down, Left};
        uint m_PrevNonSubHitDirectionId = 0;

        Vector3 GetDirection(uint dirInt)
        {
            dirInt = dirInt % (uint)System.Enum.GetValues(typeof(Direction)).Length;
            switch (dirInt)
            {
                case (uint)Direction.Up: return transform.up;
                case (uint)Direction.Right: return transform.right;
                case (uint)Direction.Down: return -transform.up;
                case (uint)Direction.Left: return -transform.right;
            }
            return Vector3.zero;
        }


        bool IsHitValid(HitResult hit)
        {
            if (hit.hasCollider)
            {
                float dot = Vector3.Dot(hit.normal, -transform.forward);
                return dot >= maxSurfaceDot;
            }
            return false;
        }

        protected override bool OnProcessOcclusion(ProcessOcclusionSource source)
        {
#if UNITY_EDITOR
            editorDebugData.lastFrameUpdate = Time.frameCount;
#endif
            var bestHit = GetBestHit(transform.position, transform.forward);

            if (IsHitValid(bestHit))
            {
                if (minSurfaceRatio > 0.5f)
                {
                    for (uint i = 0; i < (uint)System.Enum.GetValues(typeof(Direction)).Length; i++)
                    {
                        var dir3 = GetDirection(i + m_PrevNonSubHitDirectionId);
                        var startPt = transform.position + dir3 * m_Master.coneRadiusStart * (minSurfaceRatio * 2 - 1);
                        var newPt = transform.position + transform.forward * m_Master.fallOffEnd + dir3 * m_Master.coneRadiusEnd * (minSurfaceRatio * 2 - 1);

                        var bestHitSub = GetBestHit(startPt, newPt - startPt);
                        if (IsHitValid(bestHitSub))
                        {
                            if (bestHitSub.distance > bestHit.distance)
                            {
                                bestHit = bestHitSub;
                            }
                        }
                        else
                        {
                            m_PrevNonSubHitDirectionId = i;
                            bestHit = null;
                            break;
                        }
                    }
                }
            }
            else
            {
                bestHit = null;
            }

            SetHit(bestHit);
            return bestHit != null;
        }

        void SetHit(HitResult hit)
        {
            if (hit == null)
            {
                SetClippingPlaneOff();
            }
            else
            {
                switch (planeAlignment)
                {
                    case PlaneAlignment.Beam:
                        SetClippingPlane(new Plane(-transform.forward, hit.point));
                        break;
                    case PlaneAlignment.Surface:
                    default:
                        SetClippingPlane(new Plane(hit.normal, hit.point));
                        break;
                }
            }

            currentHit = hit;
        }

        protected override void OnModifyMaterialCallback(MaterialModifier.Interface owner)
        {
            Debug.Assert(owner != null);
            var planeWS = planeEquationWS;
            owner.SetMaterialProp(ShaderProperties.ClippingPlaneWS, new Vector4(planeWS.normal.x, planeWS.normal.y, planeWS.normal.z, planeWS.distance));
            owner.SetMaterialProp(ShaderProperties.ClippingPlaneProps, fadeDistanceToSurface);
        }

        void SetClippingPlane(Plane planeWS)
        {
            planeWS = planeWS.TranslateCustom(planeWS.normal * planeOffset);
            SetPlaneWS(planeWS);
            m_Master._INTERNAL_SetDynamicOcclusionCallback(GetShaderKeyword(), OnModifyMaterialCallback);
        }

        void SetClippingPlaneOff()
        {
            SetPlaneWS(new Plane());
            m_Master._INTERNAL_SetDynamicOcclusionCallback(GetShaderKeyword(), null);
        }

        void SetPlaneWS(Plane planeWS)
        {
            planeEquationWS = planeWS;

#if UNITY_EDITOR
            m_DebugPlaneLocal = planeWS;
            if (m_DebugPlaneLocal.IsValid())
            {
                float dist;
                if (m_DebugPlaneLocal.Raycast(new Ray(transform.position, transform.forward), out dist))
                    m_DebugPlaneLocal.distance = dist; // compute local distance
            }
#endif
        }

#if UNITY_EDITOR
        void LateUpdate()
        {
            if (!Application.isPlaying)
            {
                // In Editor, process raycasts at each frame update
                if (!editorRaycastAtEachFrame)
                    SetHit(null);
                else
                    ProcessOcclusion(ProcessOcclusionSource.EditorUpdate);
            }
        }

        Plane m_DebugPlaneLocal;

        void OnDrawGizmos()
        {
            if (!editorShowDebugPlane)
                return;

            if (m_DebugPlaneLocal.IsValid())
            {
                var planePos = transform.position + m_DebugPlaneLocal.distance * transform.forward;
                float planeSize = Mathf.Lerp(m_Master.coneRadiusStart, m_Master.coneRadiusEnd, Mathf.InverseLerp(0f, m_Master.fallOffEnd, m_DebugPlaneLocal.distance));

                Utils.GizmosDrawPlane(
                    m_DebugPlaneLocal.normal,
                    planePos,
                    m_Master.color.Opaque(),
                    planeSize);

                UnityEditor.Handles.color = m_Master.color.Opaque();
                UnityEditor.Handles.DrawWireDisc(planePos,
                                                m_DebugPlaneLocal.normal,
                                                planeSize * (minSurfaceRatio * 2 - 1));
            }
        }
#endif
    }
}
