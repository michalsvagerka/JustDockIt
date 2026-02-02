using System;
using System.Linq;
using UnityEngine;


[KSPAddon(KSPAddon.Startup.Flight, false)]
public sealed class JustDockIt : MonoBehaviour
{
    // Tweakables
    private const float MaxSnapDistanceMeters = 500f;
    private const float AlignSeparationMeters = 1.0f;
    private const float ClosingSpeed = 0.15f;
    
    // Hotkey: Ctrl+Alt+D 
    private static bool HotkeyPressed() => (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
            && (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt))
            && Input.GetKeyDown(KeyCode.D);

    public void Update()
    {
        if (!HighLogic.LoadedSceneIsFlight) return;
        if (!HotkeyPressed()) return;

        try
        {
            AttemptSnapDock();
        }
        catch (Exception ex)
        {
            ScreenMessages.PostScreenMessage(
                $"[JustDockIt] Error: {ex.Message}",
                6f, ScreenMessageStyle.UPPER_CENTER);
            Debug.LogException(ex);
        }
    }

    private void AttemptSnapDock()
    {
        Vessel active = FlightGlobals.ActiveVessel;
        if (active == null)
        {
            Fail("No active vessel.");
            return;
        }

        Part controlPart = active.GetReferenceTransformPart();
        if (controlPart == null)
        {
            Fail("No source selected. Use 'Control from here' on the source docking port..");
            return;
        }

        // Source docking node must be on that part.
        ModuleDockingNode srcNode = controlPart.FindModulesImplementing<ModuleDockingNode>().FirstOrDefault();
        if (srcNode == null)
        {
            Fail("The source is not a docking port. Use 'Control from here' on the source docking port.");
            return;
        }

        // (2) Detect "Set as target"
        ITargetable tgt = FlightGlobals.fetch?.VesselTarget;
        if (tgt == null)
        {
            Fail("No target selected. Use 'Set as Target' on the destination docking port.");
            return;
        }

        // Require the target be a docking node 
        ModuleDockingNode dstNode = tgt as ModuleDockingNode;
        if (dstNode == null)
        {
            Fail("The target is not a docking port. Use 'Set as Target' on the destination docking port.");
            return;
        }

        Vessel dstVessel = dstNode.vessel;
        if (dstVessel == null)
        {
            Fail("Target docking port has no vessel.");
            return;
        } 
        
        if (dstVessel == active)
        {
            Fail("Target docking port is on the same vessel.");
            return;
        }

        if (!active.loaded || !dstVessel.loaded)
        {
            Fail("Both vessels must be loaded (in physics range).");
            return;
        }

        if (IsSurfaceOrPrelaunch(active) || IsSurfaceOrPrelaunch(dstVessel))
        {
            Fail("Surface / prelaunch situations are not supported (orbit-only for now).");
            return;
        }

        // Port availability
        if (srcNode.otherNode != null || !string.Equals(srcNode.state, "Ready", StringComparison.OrdinalIgnoreCase))
        {
            Fail($"Source port not ready (state={srcNode.state}).");
            return;
        }

        if (dstNode.otherNode != null || !string.Equals(dstNode.state, "Ready", StringComparison.OrdinalIgnoreCase))
        {
            Fail($"Target port not ready (state={dstNode.state}).");
            return;
        }

        // Compatibility check using nodeType.
        if (!PortsCompatible(srcNode, dstNode))
        {
            Fail($"Ports not compatible (src={srcNode.nodeType}, dst={dstNode.nodeType}).");
            return;
        }

        if (srcNode.nodeTransform == null || dstNode.nodeTransform == null)
        {
            Fail("Docking node transforms not available (nodeTransform is null).");
            return;
        }

        float dist = Vector3.Distance(srcNode.nodeTransform.position, dstNode.nodeTransform.position);
        if (dist > MaxSnapDistanceMeters)
        {
            Fail($"Too far apart ({dist:F1} m). Get within {MaxSnapDistanceMeters:F0} m first.");
            return;
        }

        Rigidbody rb = active.rootPart?.Rigidbody;
        if (rb == null)
        {
            Fail("Active vessel has no root rigidbody (rb).");
            return;
        }
        
        Transform srcT = srcNode.nodeTransform;
        Transform dstT = dstNode.nodeTransform;

        // Desired: src forward points opposite dst forward; src up matches dst up
        Vector3 dstForward = dstT.forward;
        Vector3 dstUp = dstT.up;

        // Rotate vessel so srcT.forward aligns with -dstForward
        Quaternion q1 = Quaternion.FromToRotation(srcT.forward, -dstForward);
        Quaternion rotated = q1 * rb.rotation;
    
        // After q1, fix roll by aligning src up with dst up around the docking axis
        Vector3 srcUpAfter = (q1 * srcT.up);
        float rollAngle = Vector3.SignedAngle(srcUpAfter, dstUp, -dstForward);
        Quaternion qRoll = Quaternion.AngleAxis(rollAngle, -dstForward);

        Quaternion finalRot = qRoll * rotated;

        // Now place vessel so the *source node position* is AlignSeparationMeters away from target node along -dstForward
        Vector3 desiredSrcPos = dstT.position + dstForward.normalized * AlignSeparationMeters;
        Vector3 positionDelta = desiredSrcPos - srcT.position;
    
        // rotate and move the body (on rails to prevent excessive g-forces)
        active.GoOnRails();
        active.SetRotation(finalRot);
        active.SetPosition(rb.position + positionDelta);
        active.GoOffRails();
    
        active.angularVelocity = Vector3.zero;

        // eliminate any speed difference + add a tiny bit of speed in the docking direction
        active.ChangeWorldVelocity(dstNode.vessel.rootPart.rb.velocity - active.rootPart.rb.velocity - dstForward * ClosingSpeed);
    
        // disable automatic controls so that they don't fight magnets
        active.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
        active.ActionGroups.SetGroup(KSPActionGroup.RCS, false);

        ScreenMessages.PostScreenMessage("[JustDockIt] Aligned (closing slowly).", 4f, ScreenMessageStyle.UPPER_CENTER);
    }

    private static bool PortsCompatible(ModuleDockingNode a, ModuleDockingNode b)
    {
        if (string.Equals(a.nodeType, b.nodeType, StringComparison.OrdinalIgnoreCase))
            return true;

        if (a.nodeTypes != null && a.nodeTypes.Contains(b.nodeType))
            return true;

        if (b.nodeTypes != null && b.nodeTypes.Contains(a.nodeType))
            return true;

        return false;
    }

    private static bool IsSurfaceOrPrelaunch(Vessel v)
    {
        return v.situation == Vessel.Situations.LANDED
            || v.situation == Vessel.Situations.SPLASHED
            || v.situation == Vessel.Situations.PRELAUNCH;
    }

    private static void Fail(string msg)
    {
        ScreenMessages.PostScreenMessage($"[JustDockIt] {msg}", 6f, ScreenMessageStyle.UPPER_CENTER);
        Debug.Log($"[JustDockIt] {msg}");
    }
    
}

