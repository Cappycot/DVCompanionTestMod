using System.IO;
using System.Reflection;
using UnityEngine;
using UnityModManagerNet;
using VRM;

namespace DVCompanionTestMod
{
    public static class Main
    {
        private static UnityModManager.ModEntry mod;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            mod = modEntry;
            mod.OnUpdate = OnUpdate;
            return true;
        }

        private static AssetBundle _animationsBundle;
        private static AssetBundle _shadersBundle;
        private static RuntimeAnimatorController _runtimeAnimatorController;

        public static bool LoadAssetBundles()
        {
            _animationsBundle = _animationsBundle ?? AssetBundle.LoadFromFile(mod.Path + "Resources/animations");
            if (_animationsBundle == null)
            {
                mod.Logger.Log("Unable to load animations asset bundle.");
                return false;
            }
            _runtimeAnimatorController = _runtimeAnimatorController ?? _animationsBundle.LoadAsset<RuntimeAnimatorController>("Assets/Animations/Animator.controller");
            if (_runtimeAnimatorController == null)
            {
                mod.Logger.Log("Could not find animator controller in asset bundle.");
                return false;
            }
            _shadersBundle = _shadersBundle ?? AssetBundle.LoadFromFile(mod.Path + "Resources/vrmshaders");
            if (_shadersBundle == null)
            {
                mod.Logger.Log("Unable to load VRM shaders asset bundle.");
                return false;
            }
            DerailVRM.LoadShaders(_shadersBundle);
            return true;
        }

        // TODO: Move these vars into a new class.
        private static readonly float ASSISTANT_RADIUS = 0.25f;
        private static Animator _assistantAnimator;
        private static CharacterController _assistantController;
        private static FootstepsAudio _assistantFootsteps;
        private static VRMBlendShapeProxy _assistantBSP;
        private static FastList<VRMSpringBone> _assistantSBs;
        private static BlendShapeKey _blinkKey;

        public static GameObject MakeAssistant()
        {
            if (_assistantFootsteps == null)
            {
                CustomFirstPersonController cfpc = GameObject.FindObjectOfType<CustomFirstPersonController>();
                if (cfpc != null)
                    _assistantFootsteps = (FootstepsAudio)typeof(CustomFirstPersonController).GetField("footstepsAudio", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(cfpc);
                else
                {
                    mod.Logger.Log("Unable to find first person controller object.");
                    return null;
                }
            }
            if (_assistantFootsteps == null)
            {
                mod.Logger.Log("Unable to get footsteps audio object from controller.");
                return null;
            }

            // Find VRM file.
            string[] filenames = Directory.GetFiles(mod.Path, "*.vrm");
            if (filenames.Length < 1)
            {
                mod.Logger.Log("No .vrm files found.");
                return null;
            }

            GameObject vrm = DerailVRM.CreateVRM(filenames[0]);
            vrm.layer = 9; // This is the player layer. (27 is world item)

            Rigidbody rb = vrm.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            // TODO: Allow variable height instead of default 1.62m player height.
            BoxCollider bc = vrm.AddComponent<BoxCollider>();
            bc.center = new Vector3(0f, 0.8f, 0f);
            bc.size = new Vector3(ASSISTANT_RADIUS, 1.6f, ASSISTANT_RADIUS);
            _assistantController = vrm.AddComponent<CharacterController>();
            _assistantController.center = new Vector3(0f, 0.8f, 0f);
            _assistantController.height = 1.44f;
            _assistantController.radius = ASSISTANT_RADIUS;
            _assistantController.slopeLimit = 75f;
            _assistantController.stepOffset = 0.4f;

            _assistantAnimator = vrm.GetComponent<Animator>();
            _assistantAnimator.applyRootMotion = false;
            _assistantAnimator.runtimeAnimatorController = _runtimeAnimatorController;

            _assistantBSP = vrm.GetComponent<VRMBlendShapeProxy>();
            _blinkKey = BlendShapeKey.CreateFromPreset(BlendShapePreset.Blink);

            // When re-instantiating VRM, the LeftEye and RightEye are made null, so we need to set them again.
            VRMLookAtBoneApplyer ba = vrm.GetComponent<VRMLookAtBoneApplyer>();
            if (ba)
            {
                ba.LeftEye.Transform = OffsetOnTransform.Create(_assistantAnimator.GetBoneTransform(HumanBodyBones.LeftEye)).Transform;
                ba.RightEye.Transform = OffsetOnTransform.Create(_assistantAnimator.GetBoneTransform(HumanBodyBones.RightEye)).Transform;
            }

            _assistantSBs = new FastList<VRMSpringBone>();
            _assistantSBs.AddRange(vrm.GetComponentsInChildren<VRMSpringBone>(true));

            return vrm;
        }

        private static AssistantModState _modState = AssistantModState.GAME_LOADING;
        private static GameObject _assistant;

        private static float blinkTime = 0f;
        private static readonly float TOTAL_BLINK_TIME = 0.2f;
        private static float timeSinceLastBlink = 0f;
        private static float timeToNextBlink = 0f;
        private static float distToNextStep = STEP_IDLE_DISTANCE;
        private static readonly float STEP_IDLE_DISTANCE = 0.2f;
        private static readonly float STEP_WALK_DISTANCE = 2f * 0.5f;
        private static readonly float STEP_RUN_DISTANCE = 6f * 0.3f;
        private static readonly float STEP_THR_DISTANCE = 0.01f;

        public static void OnUpdate(UnityModManager.ModEntry modEntry, float delta)
        {
            switch (_modState)
            {
                case AssistantModState.GAME_LOADING:
                    _modState = !LoadingScreenManager.IsLoading && WorldStreamingInit.IsLoaded
                        ? AssistantModState.ASSETBUNDLE_LOADING : AssistantModState.GAME_LOADING;
                    break;

                case AssistantModState.ASSETBUNDLE_LOADING:
                    _modState = LoadAssetBundles()
                        ? AssistantModState.ASSISTANT_CREATION : AssistantModState.ERROR;
                    break;

                case AssistantModState.ASSISTANT_CREATION:
                    if (PlayerManager.PlayerCamera == null || PlayerManager.PlayerTransform == null) // Parts of the world are started up before the player.
                        return;
                    _assistant = MakeAssistant();
                    if (_assistant == null)
                    {
                        _modState = AssistantModState.ERROR;
                        return;
                    }
                    VRMLookAtHead lookat = _assistant.GetComponent<VRMLookAtHead>();
                    lookat.UpdateType = UpdateType.LateUpdate;
                    lookat.Target = PlayerManager.PlayerCamera.transform;
                    _modState = AssistantModState.ASSISTANT_LOADED;
                    break;

                case AssistantModState.ASSISTANT_LOADED:
                    Vector3 pp = PlayerManager.PlayerTransform.position;
                    Vector3 diff = pp - _assistant.transform.position;
                    Vector3 diff2 = diff;
                    diff.y = 0f;
                    float dist = diff.sqrMagnitude;

                    _assistant.transform.rotation = Quaternion.LookRotation(diff);
                    Transform parent = PlayerManager.PlayerTransform.parent;
                    int animSpeed = 0;
                    bool run = false;
                    // TODO: Figure out how train car parenting works.
                    if (diff2.sqrMagnitude > 400 || _assistant.transform.parent != parent) // 20)
                    {
                        _assistantController.enabled = false;
                        _assistant.transform.position = pp
                            - Vector3.Normalize(new Vector3(_assistant.transform.forward.x, 0f, _assistant.transform.forward.z)) * 0.5f;
                        _assistant.transform.SetParent(parent);
                        foreach (VRMSpringBone sb in _assistantSBs)
                            sb.m_center = parent;
                        _assistantController.enabled = true;
                    }
                    else if (dist > 25) // 5)
                    {
                        _assistantController.enabled = true;
                        animSpeed = 6;
                        run = true;
                        if (_assistant.transform.parent != null && !_assistantController.isGrounded)
                            _assistantController.Move(_assistant.transform.forward * 6f * delta);
                        else
                            _assistantController.SimpleMove(_assistant.transform.forward * 6f);
                    }
                    else if (dist > 4) // 2)
                    {
                        _assistantController.enabled = true;
                        animSpeed = 2;
                        if (_assistant.transform.parent != null && !_assistantController.isGrounded)
                            _assistantController.Move(_assistant.transform.forward * 2f * delta);
                        else
                            _assistantController.SimpleMove(_assistant.transform.forward * 2f);
                    }
                    else if (dist < 0.25f) // 0.5f
                    {
                        _assistantController.enabled = true;
                        animSpeed = -2;
                        if (_assistant.transform.parent != null && !_assistantController.isGrounded)
                            _assistantController.Move(-_assistant.transform.forward * 2f * delta);
                        else
                            _assistantController.SimpleMove(-_assistant.transform.forward * 2f);
                    }
                    else if (_assistantController.velocity.x != 0f || _assistantController.velocity.z != 0f || !_assistantController.isGrounded)
                    {
                        _assistantController.enabled = true;
                        _assistantController.SimpleMove(Vector3.zero);
                    }
                    else
                        _assistantController.enabled = false;

                    // Blinking anim
                    timeSinceLastBlink += delta;
                    if (blinkTime > 0f)
                    {
                        _assistantBSP.ImmediatelySetValue(_blinkKey, Mathf.Sin(Mathf.PI * blinkTime / TOTAL_BLINK_TIME));
                        blinkTime -= delta;
                    }
                    else
                        _assistantBSP.ImmediatelySetValue(_blinkKey, 0f);
                    if (timeSinceLastBlink > timeToNextBlink)
                    {
                        blinkTime = TOTAL_BLINK_TIME;
                        timeSinceLastBlink = 0f;
                        timeToNextBlink = Random.Range(2f, 4f);
                    }
                    _assistantAnimator.SetInteger("Speed", animSpeed);

                    // Footsep [sic] sound
                    float speed = _assistantController.velocity.magnitude;
                    float travl = speed * delta;
                    if (speed < STEP_THR_DISTANCE)
                        distToNextStep = STEP_IDLE_DISTANCE;
                    distToNextStep -= travl;
                    if (distToNextStep < 0f && _assistantController.isGrounded && _assistantFootsteps != null)
                    {
                        _assistantFootsteps.RequestPlayFootsepSound(
                            run ? FootstepsAudio.MovementType.Running : FootstepsAudio.MovementType.Walking,
                            _assistant.transform.position,
                            speed,
                            _assistantController.radius - 0.015f,
                            audioParent: _assistant.transform);
                        distToNextStep = run ? STEP_RUN_DISTANCE : STEP_WALK_DISTANCE;
                    }
                    break;

                default:
                    break;
            }
        }
    }
}
