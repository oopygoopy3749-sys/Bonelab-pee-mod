using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;
using UnityEngine.XR;

[assembly: MelonInfo(typeof(PeeMod.PeeModMain), "PeeMod", "1.0.0", "YourName")]
[assembly: MelonGame("Stress Level Zero", "BONELAB")]

namespace PeeMod
{
    /// <summary>
    /// A Postal 2 - inspired "relief" mechanic for BONELAB.
    /// Hold the configured button to pee a stream from roughly hip height in the
    /// direction you're looking. A puddle grows on the ground beneath you while active.
    ///
    /// IMPORTANT — READ THIS BEFORE YOU ASK "WHY DOESN'T IT WORK":
    /// BONELAB's exact internal rig/player classes (RigManager, controller rig, etc.)
    /// change between game updates and are not safely guessable without dnSpy access
    /// to your specific build's dumped assemblies. To make this mod work out-of-the-box
    /// on both PCVR and Quest without needing those exact class names, everything below
    /// is built on generic Unity XR APIs (Camera.main + UnityEngine.XR.InputDevices),
    /// which are stable across BONELAB versions. If you want the stream to originate
    /// from an exact hip bone instead of an offset below the headset, see the
    /// README section "Wiring up the real rig" for how to swap that one method out.
    /// </summary>
    public class PeeModMain : MelonMod
    {
        // ---------- Config ----------
        static MelonPreferences_Category prefCategory;
        static MelonPreferences_Entry<XRNode> prefHand;
        static MelonPreferences_Entry<float> prefFlowStrength;
        static MelonPreferences_Entry<bool> prefToggleMode; // false = hold, true = press to toggle
        static MelonPreferences_Entry<Color> prefStreamColor;

        // ---------- Runtime state ----------
        bool isPeeing = false;
        bool lastButtonState = false;

        LineRenderer stream;
        AudioSource audioSource;
        GameObject puddleObj;
        MeshRenderer puddleRenderer;
        float puddleScale = 0f;
        const float MAX_PUDDLE_SCALE = 1.4f;
        const float PUDDLE_GROW_RATE = 0.35f; // scale units per second

        Vector3 lockedPuddlePos;
        bool havePuddlePos;

        public override void OnInitializeMelon()
        {
            prefCategory = MelonPreferences.CreateCategory("PeeMod");
            prefHand = prefCategory.CreateEntry("Hand", XRNode.RightHand,
                "Hand", "Which controller's button triggers the mod (LeftHand or RightHand)");
            prefFlowStrength = prefCategory.CreateEntry("FlowStrength", 3.5f,
                "FlowStrength", "How far the stream arcs out, in meters");
            prefToggleMode = prefCategory.CreateEntry("ToggleMode", false,
                "ToggleMode", "false = hold button to pee, true = press once to start/stop");
            prefStreamColor = prefCategory.CreateEntry("StreamColor", new Color(0.95f, 0.85f, 0.15f, 0.85f),
                "StreamColor", "Color of the stream/puddle");

            BuildEffectObjects();
            MelonLogger.Msg("PeeMod loaded. Hold/press the configured controller button to relieve yourself.");
        }

        void BuildEffectObjects()
        {
            // --- Stream (LineRenderer) ---
            var streamObj = new GameObject("PeeMod_Stream");
            UnityEngine.Object.DontDestroyOnLoad(streamObj);
            stream = streamObj.AddComponent<LineRenderer>();
            stream.positionCount = 12;
            stream.widthMultiplier = 0.03f;
            stream.material = new Material(Shader.Find("Sprites/Default"));
            stream.startColor = prefStreamColor.Value;
            stream.endColor = new Color(prefStreamColor.Value.r, prefStreamColor.Value.g, prefStreamColor.Value.b, 0.15f);
            stream.enabled = false;

            // --- Audio (procedurally synthesized loop, no external file needed) ---
            audioSource = streamObj.AddComponent<AudioSource>();
            audioSource.clip = GenerateStreamNoiseClip();
            audioSource.loop = true;
            audioSource.volume = 0.5f;
            audioSource.spatialBlend = 1f;
            audioSource.playOnAwake = false;

            // --- Puddle (flattened cylinder primitive) ---
            puddleObj = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            UnityEngine.Object.DontDestroyOnLoad(puddleObj);
            UnityEngine.Object.Destroy(puddleObj.GetComponent<Collider>());
            puddleObj.transform.localScale = new Vector3(0.01f, 0.005f, 0.01f);
            puddleRenderer = puddleObj.GetComponent<MeshRenderer>();
            var mat = new Material(Shader.Find("Standard"));
            mat.color = new Color(prefStreamColor.Value.r, prefStreamColor.Value.g, prefStreamColor.Value.b, 0.55f);
            mat.SetFloat("_Glossiness", 0.9f);
            SetMaterialTransparent(mat);
            puddleRenderer.material = mat;
            puddleObj.SetActive(false);
        }

        static void SetMaterialTransparent(Material mat)
        {
            mat.SetFloat("_Mode", 3);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.renderQueue = 3000;
        }

        AudioClip GenerateStreamNoiseClip()
        {
            int sampleRate = 44100;
            float lengthSeconds = 1.0f; // loops
            int samples = (int)(sampleRate * lengthSeconds);
            var clip = AudioClip.Create("PeeMod_Stream", samples, 1, sampleRate, false);
            var data = new float[samples];
            var rng = new System.Random(12345);
            float prev = 0f;
            for (int i = 0; i < samples; i++)
            {
                float white = (float)(rng.NextDouble() * 2.0 - 1.0);
                // cheap low-pass so it sounds like running water/hiss rather than static
                prev = prev + 0.15f * (white - prev);
                data[i] = prev * 0.6f;
            }
            clip.SetData(data, 0);
            return clip;
        }

        public override void OnUpdate()
        {
            bool buttonHeld = GetButtonHeld(prefHand.Value);

            if (prefToggleMode.Value)
            {
                if (buttonHeld && !lastButtonState)
                    isPeeing = !isPeeing;
            }
            else
            {
                isPeeing = buttonHeld;
            }
            lastButtonState = buttonHeld;

            if (isPeeing)
                UpdatePeeing();
            else
                StopPeeing();
        }

        bool GetButtonHeld(XRNode node)
        {
            var device = InputDevices.GetDeviceAtXRNode(node);
            if (!device.isValid) return false;

            // Primary button (A/X) OR trigger — whichever your controller mapping fires first.
            if (device.TryGetFeatureValue(CommonUsages.primaryButton, out bool primary) && primary)
                return true;
            if (device.TryGetFeatureValue(CommonUsages.triggerButton, out bool trig) && trig)
                return true;
            return false;
        }

        void UpdatePeeing()
        {
            if (Camera.main == null) return;
            Transform head = Camera.main.transform;

            // Approximate hip-height origin: straight down from the headset, offset forward a bit.
            Vector3 origin = head.position + Vector3.down * 0.55f + head.forward * 0.08f;
            Vector3 aimDir = (head.forward + Vector3.down * 0.5f).normalized;

            if (!stream.enabled)
            {
                stream.enabled = true;
                if (!audioSource.isPlaying) audioSource.Play();
                puddleObj.SetActive(true);
                havePuddlePos = false;
            }

            // Build a simple arcing stream out of line segments (gravity-affected parabola).
            int count = stream.positionCount;
            float dist = prefFlowStrength.Value;
            for (int i = 0; i < count; i++)
            {
                float t = i / (float)(count - 1);
                Vector3 p = origin + aimDir * dist * t;
                p.y = origin.y + (aimDir.y * dist * t) - (2.0f * t * t); // gravity drop-off
                // small wiggle so it doesn't look like a laser
                p += head.right * Mathf.Sin(Time.time * 18f + i) * 0.01f;
                stream.SetPosition(i, p);
            }
            stream.transform.position = Vector3.zero; // positions are world-space already

            // Find where the stream lands and grow a puddle there.
            if (!havePuddlePos)
            {
                if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 5f))
                {
                    lockedPuddlePos = hit.point + Vector3.up * 0.01f;
                    havePuddlePos = true;
                }
                else
                {
                    lockedPuddlePos = origin + Vector3.down * 1.5f;
                    havePuddlePos = true;
                }
            }
            puddleScale = Mathf.Min(MAX_PUDDLE_SCALE, puddleScale + Time.deltaTime * PUDDLE_GROW_RATE);
            puddleObj.transform.position = lockedPuddlePos;
            puddleObj.transform.localScale = new Vector3(puddleScale, 0.005f, puddleScale);
        }

        void StopPeeing()
        {
            if (stream != null && stream.enabled)
            {
                stream.enabled = false;
                if (audioSource.isPlaying) audioSource.Stop();
                // Puddle stays on the ground and slowly shrinks/evaporates.
            }
            if (puddleObj != null && puddleObj.activeSelf && puddleScale > 0f)
            {
                puddleScale = Mathf.Max(0f, puddleScale - Time.deltaTime * 0.05f);
                puddleObj.transform.localScale = new Vector3(puddleScale, 0.005f, puddleScale);
                if (puddleScale <= 0f)
                {
                    puddleObj.SetActive(false);
                    havePuddlePos = false;
                }
            }
        }
    }
}