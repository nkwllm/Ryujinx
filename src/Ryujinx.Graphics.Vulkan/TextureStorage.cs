﻿using Ryujinx.Common;
using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Numerics;
using Format = Ryujinx.Graphics.GAL.Format;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Ryujinx.Graphics.Vulkan
{
    class TextureStorage : IDisposable
    {
        private const MemoryPropertyFlags DefaultImageMemoryFlags =
            MemoryPropertyFlags.DeviceLocalBit;

        private const ImageUsageFlags DefaultUsageFlags =
            ImageUsageFlags.SampledBit |
            ImageUsageFlags.TransferSrcBit |
            ImageUsageFlags.TransferDstBit;

        public const AccessFlags DefaultAccessMask =
            AccessFlags.ShaderReadBit |
            AccessFlags.ShaderWriteBit |
            AccessFlags.ColorAttachmentReadBit |
            AccessFlags.ColorAttachmentWriteBit |
            AccessFlags.DepthStencilAttachmentReadBit |
            AccessFlags.DepthStencilAttachmentWriteBit |
            AccessFlags.TransferReadBit |
            AccessFlags.TransferWriteBit;

        private readonly VulkanRenderer _gd;

        private readonly Device _device;

        private TextureCreateInfo _info;

        public TextureCreateInfo Info => _info;

        private readonly Image _image;
        private readonly Auto<DisposableImage> _imageAuto;
        private readonly Auto<MemoryAllocation> _allocationAuto;
        private Auto<MemoryAllocation> _foreignAllocationAuto;

        private Dictionary<Format, TextureStorage> _aliasedStorages;

        private AccessFlags _lastModificationAccess;
        private PipelineStageFlags _lastModificationStage;
        private AccessFlags _lastReadAccess;
        private PipelineStageFlags _lastReadStage;

        private int _viewsCount;
        private readonly ulong _size;

        public VkFormat VkFormat { get; }

        public unsafe TextureStorage(
            VulkanRenderer gd,
            Device device,
            TextureCreateInfo info,
            Auto<MemoryAllocation> foreignAllocation = null)
        {
            _gd = gd;
            _device = device;
            _info = info;

            var format = _gd.FormatCapabilities.ConvertToVkFormat(info.Format);
            var levels = (uint)info.Levels;
            var layers = (uint)info.GetLayers();
            var depth = (uint)(info.Target == Target.Texture3D ? info.Depth : 1);

            VkFormat = format;

            var type = info.Target.Convert();

            var extent = new Extent3D((uint)info.Width, (uint)info.Height, depth);

            var sampleCountFlags = ConvertToSampleCountFlags(gd.Capabilities.SupportedSampleCounts, (uint)info.Samples);

            var usage = GetImageUsage(info.Format, info.Target, gd.Capabilities.SupportsShaderStorageImageMultisample);

            var flags = ImageCreateFlags.CreateMutableFormatBit;

            // This flag causes mipmapped texture arrays to break on AMD GCN, so for that copy dependencies are forced for aliasing as cube.
            bool isCube = info.Target == Target.Cubemap || info.Target == Target.CubemapArray;
            bool cubeCompatible = gd.IsAmdGcn ? isCube : (info.Width == info.Height && layers >= 6);

            if (type == ImageType.Type2D && cubeCompatible)
            {
                flags |= ImageCreateFlags.CreateCubeCompatibleBit;
            }

            if (type == ImageType.Type3D && !gd.Capabilities.PortabilitySubset.HasFlag(PortabilitySubsetFlags.No3DImageView))
            {
                flags |= ImageCreateFlags.Create2DArrayCompatibleBit;
            }

            var imageCreateInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = type,
                Format = format,
                Extent = extent,
                MipLevels = levels,
                ArrayLayers = layers,
                Samples = sampleCountFlags,
                Tiling = ImageTiling.Optimal,
                Usage = usage,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
                Flags = flags,
            };

            gd.Api.CreateImage(device, imageCreateInfo, null, out _image).ThrowOnError();

            if (foreignAllocation == null)
            {
                gd.Api.GetImageMemoryRequirements(device, _image, out var requirements);
                var allocation = gd.MemoryAllocator.AllocateDeviceMemory(requirements, DefaultImageMemoryFlags);

                if (allocation.Memory.Handle == 0UL)
                {
                    gd.Api.DestroyImage(device, _image, null);
                    throw new Exception("Image initialization failed.");
                }

                _size = requirements.Size;

                gd.Api.BindImageMemory(device, _image, allocation.Memory, allocation.Offset).ThrowOnError();

                _allocationAuto = new Auto<MemoryAllocation>(allocation);
                _imageAuto = new Auto<DisposableImage>(new DisposableImage(_gd.Api, device, _image), null, _allocationAuto);

                InitialTransition(ImageLayout.Undefined, ImageLayout.General);
            }
            else
            {
                _foreignAllocationAuto = foreignAllocation;
                foreignAllocation.IncrementReferenceCount();
                var allocation = foreignAllocation.GetUnsafe();

                gd.Api.BindImageMemory(device, _image, allocation.Memory, allocation.Offset).ThrowOnError();

                _imageAuto = new Auto<DisposableImage>(new DisposableImage(_gd.Api, device, _image));

                InitialTransition(ImageLayout.Preinitialized, ImageLayout.General);
            }
        }

        public TextureStorage CreateAliasedColorForDepthStorageUnsafe(Format format)
        {
            var colorFormat = format switch
            {
                Format.S8Uint => Format.R8Unorm,
                Format.D16Unorm => Format.R16Unorm,
                Format.S8UintD24Unorm => Format.R8G8B8A8Unorm,
                Format.D32Float => Format.R32Float,
                Format.D24UnormS8Uint => Format.R8G8B8A8Unorm,
                Format.D32FloatS8Uint => Format.R32G32Float,
                _ => throw new ArgumentException($"\"{format}\" is not a supported depth or stencil format."),
            };

            return CreateAliasedStorageUnsafe(colorFormat);
        }

        public TextureStorage CreateAliasedStorageUnsafe(Format format)
        {
            if (_aliasedStorages == null || !_aliasedStorages.TryGetValue(format, out var storage))
            {
                _aliasedStorages ??= new Dictionary<Format, TextureStorage>();

                var info = NewCreateInfoWith(ref _info, format, _info.BytesPerPixel);

                storage = new TextureStorage(_gd, _device, info, _allocationAuto);

                _aliasedStorages.Add(format, storage);
            }

            return storage;
        }

        public static TextureCreateInfo NewCreateInfoWith(ref TextureCreateInfo info, Format format, int bytesPerPixel)
        {
            return NewCreateInfoWith(ref info, format, bytesPerPixel, info.Width, info.Height);
        }

        public static TextureCreateInfo NewCreateInfoWith(
            ref TextureCreateInfo info,
            Format format,
            int bytesPerPixel,
            int width,
            int height)
        {
            return new TextureCreateInfo(
                width,
                height,
                info.Depth,
                info.Levels,
                info.Samples,
                info.BlockWidth,
                info.BlockHeight,
                bytesPerPixel,
                format,
                info.DepthStencilMode,
                info.Target,
                info.SwizzleR,
                info.SwizzleG,
                info.SwizzleB,
                info.SwizzleA);
        }

        public Auto<DisposableImage> GetImage()
        {
            return _imageAuto;
        }

        public Image GetImageForViewCreation()
        {
            return _image;
        }

        public bool HasCommandBufferDependency(CommandBufferScoped cbs)
        {
            if (_foreignAllocationAuto != null)
            {
                return _foreignAllocationAuto.HasCommandBufferDependency(cbs);
            }
            else if (_allocationAuto != null)
            {
                return _allocationAuto.HasCommandBufferDependency(cbs);
            }

            return false;
        }

        private unsafe void InitialTransition(ImageLayout srcLayout, ImageLayout dstLayout)
        {
            CommandBufferScoped cbs;
            bool useTempCbs = !_gd.CommandBufferPool.OwnedByCurrentThread;

            if (useTempCbs)
            {
                cbs = _gd.BackgroundResources.Get().GetPool().Rent();
            }
            else
            {
                if (_gd.PipelineInternal != null)
                {
                    cbs = _gd.PipelineInternal.GetPreloadCommandBuffer();
                }
                else
                {
                    cbs = _gd.CommandBufferPool.Rent();
                    useTempCbs = true;
                }
            }

            var aspectFlags = _info.Format.ConvertAspectFlags();

            var subresourceRange = new ImageSubresourceRange(aspectFlags, 0, (uint)_info.Levels, 0, (uint)_info.GetLayers());

            var barrier = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = 0,
                DstAccessMask = DefaultAccessMask,
                OldLayout = srcLayout,
                NewLayout = dstLayout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _imageAuto.Get(cbs).Value,
                SubresourceRange = subresourceRange,
            };

            _gd.Api.CmdPipelineBarrier(
                cbs.CommandBuffer,
                PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.AllCommandsBit,
                0,
                0,
                null,
                0,
                null,
                1,
                barrier);

            if (useTempCbs)
            {
                cbs.Dispose();
            }
        }

        public static ImageUsageFlags GetImageUsage(Format format, Target target, bool supportsMsStorage)
        {
            var usage = DefaultUsageFlags;

            if (format.IsDepthOrStencil())
            {
                usage |= ImageUsageFlags.DepthStencilAttachmentBit;
            }
            else if (format.IsRtColorCompatible())
            {
                usage |= ImageUsageFlags.ColorAttachmentBit;
            }

            if (format.IsImageCompatible() && (supportsMsStorage || !target.IsMultisample()))
            {
                usage |= ImageUsageFlags.StorageBit;
            }

            return usage;
        }

        public static SampleCountFlags ConvertToSampleCountFlags(SampleCountFlags supportedSampleCounts, uint samples)
        {
            if (samples == 0 || samples > (uint)SampleCountFlags.Count64Bit)
            {
                return SampleCountFlags.Count1Bit;
            }

            // Round up to the nearest power of two.
            SampleCountFlags converted = (SampleCountFlags)(1u << (31 - BitOperations.LeadingZeroCount(samples)));

            // Pick nearest sample count that the host actually supports.
            while (converted != SampleCountFlags.Count1Bit && (converted & supportedSampleCounts) == 0)
            {
                converted = (SampleCountFlags)((uint)converted >> 1);
            }

            return converted;
        }

        public TextureView CreateView(TextureCreateInfo info, int firstLayer, int firstLevel)
        {
            return new TextureView(_gd, _device, info, this, firstLayer, firstLevel);
        }

        public void CopyFromOrToBuffer(
            CommandBuffer commandBuffer,
            VkBuffer buffer,
            Image image,
            int size,
            bool to,
            int x,
            int y,
            int dstLayer,
            int dstLevel,
            int dstLayers,
            int dstLevels,
            bool singleSlice,
            ImageAspectFlags aspectFlags,
            bool forFlush)
        {
            bool is3D = Info.Target == Target.Texture3D;
            int width = Info.Width;
            int height = Info.Height;
            int depth = is3D && !singleSlice ? Info.Depth : 1;
            int layer = is3D ? 0 : dstLayer;
            int layers = dstLayers;
            int levels = dstLevels;

            int offset = 0;

            for (int level = 0; level < levels; level++)
            {
                int mipSize = Info.GetMipSize(level);

                if (forFlush)
                {
                    mipSize = GetBufferDataLength(mipSize);
                }

                int endOffset = offset + mipSize;

                if ((uint)endOffset > (uint)size)
                {
                    break;
                }

                int rowLength = (Info.GetMipStride(level) / Info.BytesPerPixel) * Info.BlockWidth;

                var sl = new ImageSubresourceLayers(
                    aspectFlags,
                    (uint)(dstLevel + level),
                    (uint)layer,
                    (uint)layers);

                var extent = new Extent3D((uint)width, (uint)height, (uint)depth);

                int z = is3D ? dstLayer : 0;

                var region = new BufferImageCopy(
                    (ulong)offset,
                    (uint)BitUtils.AlignUp(rowLength, Info.BlockWidth),
                    (uint)BitUtils.AlignUp(height, Info.BlockHeight),
                    sl,
                    new Offset3D(x, y, z),
                    extent);

                if (to)
                {
                    _gd.Api.CmdCopyImageToBuffer(commandBuffer, image, ImageLayout.General, buffer, 1, region);
                }
                else
                {
                    _gd.Api.CmdCopyBufferToImage(commandBuffer, buffer, image, ImageLayout.General, 1, region);
                }

                offset += mipSize;

                width = Math.Max(1, width >> 1);
                height = Math.Max(1, height >> 1);

                if (Info.Target == Target.Texture3D)
                {
                    depth = Math.Max(1, depth >> 1);
                }
            }
        }

        private int GetBufferDataLength(int length)
        {
            if (NeedsD24S8Conversion())
            {
                return length * 2;
            }

            return length;
        }

        private bool NeedsD24S8Conversion()
        {
            return FormatCapabilities.IsD24S8(Info.Format) && VkFormat == VkFormat.D32SfloatS8Uint;
        }

        public void SetModification(AccessFlags accessFlags, PipelineStageFlags stage)
        {
            _lastModificationAccess = accessFlags;
            _lastModificationStage = stage;
        }

        public void InsertReadToWriteBarrier(CommandBufferScoped cbs, AccessFlags dstAccessFlags, PipelineStageFlags dstStageFlags, bool insideRenderPass)
        {
            var lastReadStage = _lastReadStage;

            if (insideRenderPass)
            {
                // We can't have barrier from compute inside a render pass,
                // as it is invalid to specify compute in the subpass dependency stage mask.

                lastReadStage &= ~PipelineStageFlags.ComputeShaderBit;
            }

            if (lastReadStage != PipelineStageFlags.None)
            {
                TextureView.InsertMemoryBarrier(
                    _gd.Api,
                    cbs.CommandBuffer,
                    _lastReadAccess,
                    dstAccessFlags,
                    lastReadStage,
                    dstStageFlags);

                _lastReadAccess = AccessFlags.None;
                _lastReadStage = PipelineStageFlags.None;
            }
        }

        public void InsertWriteToReadBarrier(CommandBufferScoped cbs, AccessFlags dstAccessFlags, PipelineStageFlags dstStageFlags)
        {
            _lastReadAccess |= dstAccessFlags;
            _lastReadStage |= dstStageFlags;

            if (_lastModificationAccess != AccessFlags.None)
            {
                TextureView.InsertMemoryBarrier(
                    _gd.Api,
                    cbs.CommandBuffer,
                    _lastModificationAccess,
                    dstAccessFlags,
                    _lastModificationStage,
                    dstStageFlags);

                _lastModificationAccess = AccessFlags.None;
            }
        }

        public void IncrementViewsCount()
        {
            _viewsCount++;
        }

        public void DecrementViewsCount()
        {
            if (--_viewsCount == 0)
            {
                _gd.PipelineInternal?.FlushCommandsIfWeightExceeding(_imageAuto, _size);

                Dispose();
            }
        }

        public void Dispose()
        {
            if (_aliasedStorages != null)
            {
                foreach (var storage in _aliasedStorages.Values)
                {
                    storage.Dispose();
                }

                _aliasedStorages.Clear();
            }

            _imageAuto.Dispose();
            _allocationAuto?.Dispose();
            _foreignAllocationAuto?.DecrementReferenceCount();
            _foreignAllocationAuto = null;
        }
    }
}
