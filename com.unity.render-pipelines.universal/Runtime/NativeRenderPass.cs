using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEngine.Rendering.Universal
{
    public partial class ScriptableRenderer
    {
        private const int kRenderPassMapSize = 10;
        private const int kRenderPassMaxCount = 20;

        // used to keep track of the index of the last pass when we called BeginSubpass
        private int m_LastBeginSubpassPassIndex = 0;

        private Dictionary<Hash128, int[]> m_MergeableRenderPassesMap = new Dictionary<Hash128, int[]>(kRenderPassMapSize);
        // static array storing all the mergeableRenderPassesMap arrays. This is used to remove any GC allocs during the frame which would have been introduced by using a dynamic array to store the mergeablePasses per RenderPass
        private int[][] m_MergeableRenderPassesMapArrays;
        private Hash128[] m_PassIndexToPassHash = new Hash128[kRenderPassMaxCount];
        private Dictionary<Hash128, int> m_RenderPassesAttachmentCount = new Dictionary<Hash128, int>(kRenderPassMapSize);

        AttachmentDescriptor[] m_ActiveColorAttachmentDescriptors = new AttachmentDescriptor[]
        {
            RenderingUtils.emptyAttachment, RenderingUtils.emptyAttachment, RenderingUtils.emptyAttachment,
            RenderingUtils.emptyAttachment, RenderingUtils.emptyAttachment, RenderingUtils.emptyAttachment,
            RenderingUtils.emptyAttachment, RenderingUtils.emptyAttachment
        };
        AttachmentDescriptor m_ActiveDepthAttachmentDescriptor;

        private static partial class Profiling
        {
            public static readonly ProfilingSampler setMRTAttachmentsList = new ProfilingSampler($"NativeRenderPass {nameof(SetNativeRenderPassMRTAttachmentList)}");
            public static readonly ProfilingSampler setAttachmentList = new ProfilingSampler($"NativeRenderPass {nameof(SetNativeRenderPassAttachmentList)}");
            public static readonly ProfilingSampler configure = new ProfilingSampler($"NativeRenderPass {nameof(ConfigureNativeRenderPass)}");
            public static readonly ProfilingSampler execute = new ProfilingSampler($"NativeRenderPass {nameof(ExecuteNativeRenderPass)}");
            public static readonly ProfilingSampler setupFrameData = new ProfilingSampler($"NativeRenderPass {nameof(SetupNativeRenderPassFrameData)}");
        }

        internal struct RenderPassDescriptor
        {
            internal int w, h, samples, depthID;

            internal RenderPassDescriptor(int width, int height, int sampleCount, int rtID)
            {
                w = width;
                h = height;
                samples = sampleCount;
                depthID = rtID;
            }
        }

        internal void ResetNativeRenderPassFrameData()
        {
            if (m_MergeableRenderPassesMapArrays == null)
                m_MergeableRenderPassesMapArrays = new int[kRenderPassMapSize][];

            for (int i = 0; i < kRenderPassMapSize; ++i)
            {
                if (m_MergeableRenderPassesMapArrays[i] == null)
                    m_MergeableRenderPassesMapArrays[i] = new int[kRenderPassMaxCount];

                for (int j = 0; j < kRenderPassMaxCount; ++j)
                {
                    m_MergeableRenderPassesMapArrays[i][j] = -1;
                }
            }
        }

        internal void SetupNativeRenderPassFrameData(CameraData cameraData, bool isRenderPassEnabled)
        {
            //TODO: edge cases to detect that should affect possible passes to merge
            // - total number of color attachment > 8

            // Go through all the passes and mark the final one as last pass

            using (new ProfilingScope(null, Profiling.setupFrameData))
            {
                int lastPassIndex = m_ActiveRenderPassQueue.Count - 1;

                // Make sure the list is already sorted!

                m_MergeableRenderPassesMap.Clear();
                m_RenderPassesAttachmentCount.Clear();
                uint currentHashIndex = 0;
                // reset all the passes last pass flag
                for (int i = 0; i < m_ActiveRenderPassQueue.Count; ++i)
                {
                    var renderPass = m_ActiveRenderPassQueue[i];

                    // Empty configure to setup dimensions/targets and whatever data is needed for merging
                    // We do not execute this at this time, so render targets are still invalid
                    var rpDesc = InitializeRenderPassDescriptor(cameraData, renderPass);

                    renderPass.isLastPass = false;
                    renderPass.renderPassQueueIndex = i;

                    Hash128 hash = CreateRenderPassHash(rpDesc, currentHashIndex);

                    m_PassIndexToPassHash[i] = hash;

                    bool RPEnabled = renderPass.useNativeRenderPass && isRenderPassEnabled;
                    if (!RPEnabled)
                        continue;

                    if (!m_MergeableRenderPassesMap.ContainsKey(hash))
                    {
                        m_MergeableRenderPassesMap.Add(hash, m_MergeableRenderPassesMapArrays[m_MergeableRenderPassesMap.Count]);
                        m_RenderPassesAttachmentCount.Add(hash, 0);
                    }
                    else if (m_MergeableRenderPassesMap[hash][GetValidPassIndexCount(m_MergeableRenderPassesMap[hash]) - 1] != (i - 1))
                    {
                        // if the passes are not sequential we want to split the current mergeable passes list. So we increment the hashIndex and update the hash

                        currentHashIndex++;
                        hash = CreateRenderPassHash(rpDesc, currentHashIndex);

                        m_PassIndexToPassHash[i] = hash;

                        m_MergeableRenderPassesMap.Add(hash, m_MergeableRenderPassesMapArrays[m_MergeableRenderPassesMap.Count]);
                        m_RenderPassesAttachmentCount.Add(hash, 0);
                    }

                    m_MergeableRenderPassesMap[hash][GetValidPassIndexCount(m_MergeableRenderPassesMap[hash])] = i;
                }

                m_ActiveRenderPassQueue[lastPassIndex].isLastPass = true;

                for (int i = 0; i < m_ActiveRenderPassQueue.Count; ++i)
                    m_ActiveRenderPassQueue[i].m_InputAttachmentIndices = new NativeArray<int>(8, Allocator.Temp);
            }
        }

        internal void SetNativeRenderPassMRTAttachmentList(ScriptableRenderPass renderPass, ref CameraData cameraData, uint validColorBuffersCount, bool needCustomCameraColorClear, ClearFlag clearFlag)
        {
            using (new ProfilingScope(null, Profiling.setMRTAttachmentsList))
            {
                int currentPassIndex = renderPass.renderPassQueueIndex;
                Hash128 currentPassHash = m_PassIndexToPassHash[currentPassIndex];
                int[] currentMergeablePasses = m_MergeableRenderPassesMap[currentPassHash];

                // Not the first pass
                if (currentMergeablePasses.First() != currentPassIndex)
                    return;

                m_RenderPassesAttachmentCount[currentPassHash] = 0;

                int currentAttachmentIdx = 0;
                foreach (var passIdx in currentMergeablePasses)
                {
                    if (passIdx == -1)
                        break;
                    ScriptableRenderPass pass = m_ActiveRenderPassQueue[passIdx];

                    for (int i = 0; i < pass.m_InputAttachmentIndices.Length; ++i)
                        pass.m_InputAttachmentIndices[i] = -1;

                    // TODO: review the lastPassToBB logic to mak it work with merged passes
                    bool isLastPassToBB = false;

                    for (int i = 0; i < validColorBuffersCount; ++i)
                    {
                        AttachmentDescriptor currentAttachmentDescriptor =
                            new AttachmentDescriptor(pass.renderTargetFormat[i] != GraphicsFormat.None ? pass.renderTargetFormat[i] : GetDefaultGraphicsFormat(cameraData));

                        // if this is the current camera's last pass, also check if one of the RTs is the backbuffer (BuiltinRenderTextureType.CameraTarget)
                        isLastPassToBB |=  pass.isLastPass && (pass.colorAttachments[i] == BuiltinRenderTextureType.CameraTarget);

                        int existingAttachmentIndex = FindAttachmentDescriptorIndexInList(currentAttachmentIdx,
                            currentAttachmentDescriptor, m_ActiveColorAttachmentDescriptors);

                        if (existingAttachmentIndex == -1)
                        {
                            // add a new attachment
                            m_ActiveColorAttachmentDescriptors[currentAttachmentIdx] = currentAttachmentDescriptor;

                            m_ActiveColorAttachmentDescriptors[currentAttachmentIdx].ConfigureTarget(pass.colorAttachments[i],  (clearFlag & ClearFlag.Color) == 0, true);

                            if ((clearFlag & ClearFlag.Color) != 0)
                            {
                                var clearColor = (needCustomCameraColorClear && pass.colorAttachments[i] == m_CameraColorTarget) ? cameraData.camera.backgroundColor : renderPass.clearColor;
                                m_ActiveColorAttachmentDescriptors[currentAttachmentIdx].ConfigureClear(CoreUtils.ConvertSRGBToActiveColorSpace(clearColor), 1.0f, 0);
                            }

                            pass.m_InputAttachmentIndices[i] = currentAttachmentIdx;

                            currentAttachmentIdx++;
                            m_RenderPassesAttachmentCount[currentPassHash]++;
                        }
                        else
                        {
                            // attachment was already present
                            pass.m_InputAttachmentIndices[i] = existingAttachmentIndex;
                        }
                    }

                    // TODO: this is redundant and is being setup for each attachment. Needs to be done only once per mergeable pass list (we need to make sure mergeable passes use the same depth!)
                    m_ActiveDepthAttachmentDescriptor = new AttachmentDescriptor(GraphicsFormat.DepthAuto);
                    m_ActiveDepthAttachmentDescriptor.ConfigureTarget(pass.depthAttachment, (clearFlag & ClearFlag.DepthStencil) == 0, !isLastPassToBB);
                    if ((clearFlag & ClearFlag.DepthStencil) != 0)
                        m_ActiveDepthAttachmentDescriptor.ConfigureClear(Color.black, 1.0f, 0);
                }
            }
        }

        internal void SetNativeRenderPassAttachmentList(ScriptableRenderPass renderPass, ref CameraData cameraData, RenderTargetIdentifier passColorAttachment, RenderTargetIdentifier passDepthAttachment, ClearFlag finalClearFlag, Color finalClearColor)
        {
            using (new ProfilingScope(null, Profiling.setAttachmentList))
            {
                int currentPassIndex = renderPass.renderPassQueueIndex;
                Hash128 currentPassHash = m_PassIndexToPassHash[currentPassIndex];
                int[] currentMergeablePasses = m_MergeableRenderPassesMap[currentPassHash];

                // Skip if not the first pass
                if (currentMergeablePasses.First() != currentPassIndex)
                    return;

                m_RenderPassesAttachmentCount[currentPassHash] = 0;

                int currentAttachmentIdx = 0;
                foreach (var passIdx in currentMergeablePasses)
                {
                    if (passIdx == -1)
                        break;
                    ScriptableRenderPass pass = m_ActiveRenderPassQueue[passIdx];

                    for (int i = 0; i < pass.m_InputAttachmentIndices.Length; ++i)
                        pass.m_InputAttachmentIndices[i] = -1;

                    AttachmentDescriptor currentAttachmentDescriptor;
                    var usesTargetTexture = cameraData.targetTexture != null;
                    var depthOnly = renderPass.depthOnly || (usesTargetTexture && cameraData.targetTexture.graphicsFormat == GraphicsFormat.DepthAuto);
                    // Offscreen depth-only cameras need this set explicitly
                    if (depthOnly && usesTargetTexture)
                    {
                        if (cameraData.targetTexture.graphicsFormat == GraphicsFormat.DepthAuto && !pass.overrideCameraTarget)
                            passColorAttachment = new RenderTargetIdentifier(cameraData.targetTexture);
                        else
                            passColorAttachment = renderPass.colorAttachment;
                        currentAttachmentDescriptor = new AttachmentDescriptor(GraphicsFormat.DepthAuto);
                    }
                    else
                        currentAttachmentDescriptor =
                            new AttachmentDescriptor(cameraData.cameraTargetDescriptor.graphicsFormat);

                    if (pass.overrideCameraTarget)
                        currentAttachmentDescriptor = new AttachmentDescriptor(pass.renderTargetFormat[0] != GraphicsFormat.None ? pass.renderTargetFormat[0] : GetDefaultGraphicsFormat(cameraData));

                    var samples = pass.renderTargetSampleCount != -1
                        ? pass.renderTargetSampleCount
                        : cameraData.cameraTargetDescriptor.msaaSamples;

                    var colorAttachmentTarget =
                        (depthOnly || passColorAttachment != BuiltinRenderTextureType.CameraTarget)
                        ? passColorAttachment : (usesTargetTexture
                            ? new RenderTargetIdentifier(cameraData.targetTexture.colorBuffer)
                            : BuiltinRenderTextureType.CameraTarget);

                    var depthAttachmentTarget = (passDepthAttachment != BuiltinRenderTextureType.CameraTarget) ?
                        passDepthAttachment : (usesTargetTexture
                            ? new RenderTargetIdentifier(cameraData.targetTexture.depthBuffer)
                            : BuiltinRenderTextureType.Depth);

                    // TODO: review the lastPassToBB logic to mak it work with merged passes
                    // keep track if this is the current camera's last pass and the RT is the backbuffer (BuiltinRenderTextureType.CameraTarget)
                    // knowing isLastPassToBB can help decide the optimal store action as it gives us additional information about the current frame
                    bool isLastPassToBB = pass.isLastPass && (colorAttachmentTarget == BuiltinRenderTextureType.CameraTarget);
                    currentAttachmentDescriptor.ConfigureTarget(colorAttachmentTarget, ((uint)finalClearFlag & (uint)ClearFlag.Color) == 0, !(samples > 1 && isLastPassToBB));

                    // TODO: this is redundant and is being setup for each attachment. Needs to be done only once per mergeable pass list (we need to make sure mergeable passes use the same depth!)
                    m_ActiveDepthAttachmentDescriptor = new AttachmentDescriptor(GraphicsFormat.DepthAuto);
                    m_ActiveDepthAttachmentDescriptor.ConfigureTarget(depthAttachmentTarget,
                        ((uint)finalClearFlag & (uint)ClearFlag.Depth) == 0, !isLastPassToBB);

                    if (finalClearFlag != ClearFlag.None)
                    {
                        // We don't clear color for Overlay render targets, however pipeline set's up depth only render passes as color attachments which we do need to clear
                        if ((cameraData.renderType != CameraRenderType.Overlay || depthOnly && ((uint)finalClearFlag & (uint)ClearFlag.Color) != 0))
                            currentAttachmentDescriptor.ConfigureClear(finalClearColor, 1.0f, 0);
                        if (((uint)finalClearFlag & (uint)ClearFlag.Depth) != 0)
                            m_ActiveDepthAttachmentDescriptor.ConfigureClear(Color.black, 1.0f, 0);
                    }

                    // resolving to the implicit color target's resolve surface TODO: handle m_CameraResolveTarget if present?
                    if (samples > 1)
                        currentAttachmentDescriptor.ConfigureResolveTarget(colorAttachmentTarget);

                    int existingAttachmentIndex = FindAttachmentDescriptorIndexInList(currentAttachmentIdx,
                        currentAttachmentDescriptor, m_ActiveColorAttachmentDescriptors);

                    if (existingAttachmentIndex == -1)
                    {
                        // add a new attachment
                        pass.m_InputAttachmentIndices[0] = currentAttachmentIdx;
                        m_ActiveColorAttachmentDescriptors[currentAttachmentIdx] = currentAttachmentDescriptor;
                        currentAttachmentIdx++;
                        m_RenderPassesAttachmentCount[currentPassHash]++;
                    }
                    else
                    {
                        // attachment was already present
                        pass.m_InputAttachmentIndices[0] = existingAttachmentIndex;
                    }
                }
            }
        }

        internal void ConfigureNativeRenderPass(CommandBuffer cmd, ScriptableRenderPass renderPass, CameraData cameraData)
        {
            using (new ProfilingScope(null, Profiling.configure))
            {
                int currentPassIndex = renderPass.renderPassQueueIndex;
                Hash128 currentPassHash = m_PassIndexToPassHash[currentPassIndex];
                int[] currentMergeablePasses = m_MergeableRenderPassesMap[currentPassHash];

                // If it's the first pass, configure the whole merge block
                if (currentMergeablePasses.First() == currentPassIndex)
                {
                    foreach (var passIdx in currentMergeablePasses)
                    {
                        if (passIdx == -1)
                            break;
                        ScriptableRenderPass pass = m_ActiveRenderPassQueue[passIdx];
                        pass.Configure(cmd, cameraData.cameraTargetDescriptor);
                    }
                }
            }
        }

        internal void ExecuteNativeRenderPass(ScriptableRenderContext context, ScriptableRenderPass renderPass, CameraData cameraData, ref RenderingData renderingData)
        {
            using (new ProfilingScope(null, Profiling.execute))
            {
                int currentPassIndex = renderPass.renderPassQueueIndex;
                Hash128 currentPassHash = m_PassIndexToPassHash[currentPassIndex];
                int[] currentMergeablePasses = m_MergeableRenderPassesMap[currentPassHash];

                int validColorBuffersCount = m_RenderPassesAttachmentCount[currentPassHash];

                bool isLastPass = renderPass.isLastPass;
                // TODO: review the lastPassToBB logic to mak it work with merged passes
                // keep track if this is the current camera's last pass and the RT is the backbuffer (BuiltinRenderTextureType.CameraTarget)
                bool isLastPassToBB = isLastPass && (m_ActiveColorAttachmentDescriptors[0].loadStoreTarget ==
                    BuiltinRenderTextureType.CameraTarget);
                var depthOnly = renderPass.depthOnly || (cameraData.targetTexture != null && cameraData.targetTexture.graphicsFormat == GraphicsFormat.DepthAuto);
                bool useDepth = depthOnly || (!renderPass.overrideCameraTarget || (renderPass.overrideCameraTarget && renderPass.depthAttachment != BuiltinRenderTextureType.CameraTarget)) &&
                    (!(isLastPassToBB || (isLastPass && cameraData.camera.targetTexture != null)));

                var attachments =
                    new NativeArray<AttachmentDescriptor>(useDepth && !depthOnly ? validColorBuffersCount + 1 : 1,
                        Allocator.Temp);

                for (int i = 0; i < validColorBuffersCount; ++i)
                    attachments[i] = m_ActiveColorAttachmentDescriptors[i];

                if (useDepth && !depthOnly)
                    attachments[validColorBuffersCount] = m_ActiveDepthAttachmentDescriptor;

                var rpDesc = InitializeRenderPassDescriptor(cameraData, renderPass);

                int validPassCount = GetValidPassIndexCount(currentMergeablePasses);

                var attachmentIndicesCount = GetSubPassAttachmentIndicesCount(renderPass);

                var attachmentIndices = new NativeArray<int>(!depthOnly ? (int)attachmentIndicesCount : 0, Allocator.Temp);
                if (!depthOnly)
                {
                    for (int i = 0; i < attachmentIndicesCount; ++i)
                    {
                        attachmentIndices[i] = renderPass.m_InputAttachmentIndices[i];
                    }
                }

                if (validPassCount == 1 || currentMergeablePasses[0] == currentPassIndex) // Check if it's the first pass
                {
                    context.BeginRenderPass(rpDesc.w, rpDesc.h, Math.Max(rpDesc.samples, 1), attachments,
                        useDepth ? (!depthOnly ? validColorBuffersCount : 0) : -1);
                    attachments.Dispose();

                    context.BeginSubPass(attachmentIndices);

                    m_LastBeginSubpassPassIndex = currentPassIndex;
                }
                else
                {
                    if (!AreAttachmentIndicesCompatible(m_ActiveRenderPassQueue[m_LastBeginSubpassPassIndex], m_ActiveRenderPassQueue[currentPassIndex]))
                    {
                        context.EndSubPass();
                        context.BeginSubPass(attachmentIndices);

                        m_LastBeginSubpassPassIndex = currentPassIndex;
                    }
                }

                attachmentIndices.Dispose();

                renderPass.Execute(context, ref renderingData);

                if (validPassCount == 1 || currentMergeablePasses[validPassCount - 1] == currentPassIndex) // Check if it's the last pass
                {
                    context.EndSubPass();
                    context.EndRenderPass();

                    m_LastBeginSubpassPassIndex = 0;
                }

                for (int i = 0; i < m_ActiveColorAttachmentDescriptors.Length; ++i)
                {
                    m_ActiveColorAttachmentDescriptors[i] = RenderingUtils.emptyAttachment;
                }

                m_ActiveDepthAttachmentDescriptor = RenderingUtils.emptyAttachment;
            }
        }

        internal static uint GetSubPassAttachmentIndicesCount(ScriptableRenderPass pass)
        {
            uint numValidAttachments = 0;

            foreach (var attIdx in pass.m_InputAttachmentIndices)
            {
                if (attIdx >= 0)
                    ++numValidAttachments;
            }

            return numValidAttachments;
        }

        internal static bool AreAttachmentIndicesCompatible(ScriptableRenderPass lastSubPass, ScriptableRenderPass currentSubPass)
        {
            uint lastSubPassAttCount = GetSubPassAttachmentIndicesCount(lastSubPass);
            uint currentSubPassAttCount = GetSubPassAttachmentIndicesCount(currentSubPass);

            if (currentSubPassAttCount > lastSubPassAttCount)
                return false;

            uint numEqualAttachments = 0;
            for (int currPassIdx = 0; currPassIdx < currentSubPassAttCount; ++currPassIdx)
            {
                for (int lastPassIdx = 0; lastPassIdx < lastSubPassAttCount; ++lastPassIdx)
                {
                    if (currentSubPass.m_InputAttachmentIndices[currPassIdx] == lastSubPass.m_InputAttachmentIndices[lastPassIdx])
                        numEqualAttachments++;
                }
            }

            return (numEqualAttachments == currentSubPassAttCount);
        }

        internal static uint GetValidColorAttachmentCount(AttachmentDescriptor[] colorAttachments)
        {
            uint nonNullColorBuffers = 0;
            if (colorAttachments != null)
            {
                foreach (var attachment in colorAttachments)
                {
                    if (attachment != RenderingUtils.emptyAttachment)
                        ++nonNullColorBuffers;
                }
            }
            return nonNullColorBuffers;
        }

        internal static int FindAttachmentDescriptorIndexInList(int attachmentIdx, AttachmentDescriptor attachmentDescriptor, AttachmentDescriptor[] attachmentDescriptors)
        {
            int existingAttachmentIndex = -1;
            for (int i = 0; i < attachmentIdx; ++i)
            {
                AttachmentDescriptor att = attachmentDescriptors[i];

                if (att.loadStoreTarget == attachmentDescriptor.loadStoreTarget)
                {
                    existingAttachmentIndex = i;
                    break;
                }
            }

            return existingAttachmentIndex;
        }

        internal static int GetValidPassIndexCount(int[] array)
        {
            for (int i = 0; i < array.Length; ++i)
                if (array[i] == -1)
                    return i;
            return array.Length - 1;
        }

        internal static Hash128 CreateRenderPassHash(int width, int height, int depthID, int sample, uint hashIndex)
        {
            return new Hash128((uint)width * 10000 + (uint)height, (uint)depthID, (uint)sample, hashIndex);
        }

        internal static Hash128 CreateRenderPassHash(RenderPassDescriptor desc, uint hashIndex)
        {
            return new Hash128((uint)desc.w * 10000 + (uint)desc.h, (uint)desc.depthID, (uint)desc.samples, hashIndex);
        }

        private static RenderPassDescriptor InitializeRenderPassDescriptor(CameraData cameraData, ScriptableRenderPass renderPass)
        {
            var w = renderPass.renderTargetWidth != -1 ? renderPass.renderTargetWidth : cameraData.cameraTargetDescriptor.width;
            var h = renderPass.renderTargetHeight != -1 ? renderPass.renderTargetHeight : cameraData.cameraTargetDescriptor.height;
            var samples = renderPass.renderTargetSampleCount != -1 ? renderPass.renderTargetSampleCount : cameraData.cameraTargetDescriptor.msaaSamples;
            var depthID = renderPass.depthOnly ? renderPass.colorAttachment.GetHashCode() : renderPass.depthAttachment.GetHashCode();
            return new RenderPassDescriptor(w, h, samples, depthID);
        }

        private static GraphicsFormat GetDefaultGraphicsFormat(CameraData cameraData)
        {
            GraphicsFormat hdrFormat = GraphicsFormat.None;
            if (cameraData.isHdrEnabled)
            {
                if (!Graphics.preserveFramebufferAlpha &&
                    RenderingUtils.SupportsGraphicsFormat(GraphicsFormat.B10G11R11_UFloatPack32,
                        FormatUsage.Linear | FormatUsage.Render))
                    hdrFormat = GraphicsFormat.B10G11R11_UFloatPack32;
                else if (RenderingUtils.SupportsGraphicsFormat(GraphicsFormat.R16G16B16A16_SFloat,
                    FormatUsage.Linear | FormatUsage.Render))
                    hdrFormat = GraphicsFormat.R16G16B16A16_SFloat;
                else
                    hdrFormat = SystemInfo.GetGraphicsFormat(DefaultFormat.HDR);
            }

            return cameraData.isHdrEnabled ? hdrFormat : SystemInfo.GetGraphicsFormat(DefaultFormat.LDR);
        }
    }
}
