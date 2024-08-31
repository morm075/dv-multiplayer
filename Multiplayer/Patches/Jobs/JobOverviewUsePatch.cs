using DV;
using DV.Interaction;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.Utils;
using HarmonyLib;
using Multiplayer.Components;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.Jobs;
using Multiplayer.Utils;
using System.Collections;
using Unity.Jobs;
using UnityEngine;
using static UnityEngine.GraphicsBuffer;

namespace Multiplayer.Patches.Jobs;

[HarmonyPatch(typeof(JobValidator))]
public static class JobValidator_Patch
{
    [HarmonyPatch(nameof(JobValidator.ProcessJobOverview))]
    private static bool Prefix(JobValidator __instance, JobOverview jobOverview)
    {
        if (!NetworkLifecycle.Instance.IsHost())
        {
            __instance.bookletPrinter.PlayErrorSound();
            return false;
        }

        return true;

        /*
        JobValidator component = target.GetComponent<JobValidator>();
        if (component == null)
            return false;

        if (component.bookletPrinter.IsOnCooldown)
        {
            component.bookletPrinter.PlayErrorSound();
            return false;
        }

        Job job = ___jobOverview.job;

        Multiplayer.Log($"JobOverviewUse_HandleUse_Patch jobId: {job.ID}");

        NetworkedJob networkedJob;

        if (!NetworkedJob.TryGetFromJob(job, out networkedJob))
        {
            Multiplayer.Log($"JobOverviewUse_HandleUse_Patch No netId found for jobId: {job.ID}");
            component.bookletPrinter.PlayErrorSound();
            return false;
        }

        if(networkedJob.allowTake == true) {
            Multiplayer.Log($"JobOverviewUse_HandleUse_Patch jobId: {job.ID}, Take allowed: {networkedJob.allowTake}");
            return true;
        }
        else if (networkedJob.allowTake == null || (networkedJob.allowTake == false && networkedJob.takenBy == null))
        {
            Multiplayer.Log($"JobOverviewUse_HandleUse_Patch WaitForResponse returned for jobId: {job.ID}");
            networkedJob.jobValidator = component;
            networkedJob.jobOverview = ___jobOverview;
            NetworkLifecycle.Instance.Client.SendJobTakeRequest(networkedJob.NetId);

            return false;

        }

        component.bookletPrinter.PlayErrorSound();
        return false;
        */
    }
}
