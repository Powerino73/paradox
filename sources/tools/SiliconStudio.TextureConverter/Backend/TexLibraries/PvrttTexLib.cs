﻿// Copyright (c) 2014 Silicon Studio Corp. (http://siliconstudio.co.jp)
// This file is distributed under GPL v3. See LICENSE.md for details.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security;
using System.IO;
using SiliconStudio.Core.Diagnostics;
using SiliconStudio.TextureConverter.PvrttWrapper;
using SiliconStudio.TextureConverter.Requests;

namespace SiliconStudio.TextureConverter.TexLibraries
{

    /// <summary>
    /// Class containing the needed native Data used by PVR Texture library
    /// </summary>
    internal class PvrTextureLibraryData : ITextureLibraryData
    {
        /// <summary>
        /// A <see cref="PVRTexture" /> instance
        /// </summary>
        public PVRTexture Texture;


        /// <summary>
        /// The corresponding <see cref="PVRTextureHeader" /> to the <see cref="PVRTexture" /> above.
        /// </summary>
        public PVRTextureHeader Header;
    }

    /// <summary>
    /// Peforms requests from <see cref="TextureTool" /> using PVR Texture Tool.
    /// </summary>
    internal class PvrttTexLib : ITexLibrary
    {
        private static object lockObject = new object();
        private static Logger Log = GlobalLogger.GetLogger("PvrttTexLib");

        /// <summary>
        /// Initializes a new instance of the <see cref="PvrttTexLib"/> class.
        /// </summary>
        public PvrttTexLib() {}

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources. Nothing in this case
        /// </summary>
        public void Dispose() {}


        public void Dispose(TexImage image)
        {
            if (!image.LibraryData.ContainsKey(this)) return;
            PvrTextureLibraryData libraryData = (PvrTextureLibraryData)image.LibraryData[this];

            if (libraryData.Texture != null)
            {
                libraryData.Header = null;
                libraryData.Texture.Dispose();
            }
        }

        
        public bool SupportBGRAOrder()
        {
            return false;
        }

        public bool CanHandleRequest(TexImage image, IRequest request)
        {
            switch (request.Type)
            {
                    // Loading only file with a .pvr extension
                case RequestType.Loading:
                    LoadingRequest loader = (LoadingRequest)request;
                    return loader.Mode == LoadingRequest.LoadingMode.FilePath && (Path.GetExtension(loader.FilePath).Equals(".pvr") || Path.GetExtension(loader.FilePath).Equals(".ktx"));

                case RequestType.Compressing:
                    CompressingRequest compress = (CompressingRequest)request;
                    return SupportFormat(compress.Format) && SupportFormat(image.Format);

                case RequestType.Export:
                    ExportRequest export = (ExportRequest)request;
                    return SupportFormat(image.Format) && (Path.GetExtension(export.FilePath).Equals(".pvr") || Path.GetExtension(export.FilePath).Equals(".ktx"));

                case RequestType.Decompressing:
                    return SupportFormat(image.Format);

                case RequestType.MipMapsGeneration:
                    return ((MipMapsGenerationRequest)request).Filter != Filter.MipMapGeneration.Box;

                case RequestType.Rescaling:
                    Filter.Rescaling filter = ((RescalingRequest)request).Filter;
                    return filter == Filter.Rescaling.Bicubic || filter == Filter.Rescaling.Bilinear || filter == Filter.Rescaling.Nearest; 

                case RequestType.PreMultiplyAlpha:
                case RequestType.SwitchingChannels:
                case RequestType.Flipping:
                case RequestType.NormalMapGeneration:
                    return true;

                default:
                    return false;
            }
        }


        public void StartLibrary(TexImage image)
        {
            PvrTextureLibraryData libraryData = new PvrTextureLibraryData();

            int imageArraySize = image.Dimension == TexImage.TextureDimension.TextureCube ? image.ArraySize/6 : image.ArraySize;
            int imageFaceCount = image.Dimension == TexImage.TextureDimension.TextureCube ? 6 : 1;

            // Creating native header corresponding to the TexImage instance
            ulong format = RetrieveNativeFormat(image.Format);
            EPVRTColourSpace colorSpace = RetrieveNativeColorSpace(image.Format);
            EPVRTVariableType pixelType = RetrieveNativePixelType(image.Format);
            libraryData.Header = new PVRTextureHeader(format, image.Height, image.Width, image.Depth, image.MipmapCount, imageArraySize, imageFaceCount, colorSpace, pixelType);
            
            int imageCount = 0;
            int depth = image.Depth;
            libraryData.Texture = new PVRTexture(libraryData.Header, IntPtr.Zero); // Initializing a new native texture, allocating memory.

            // Copying TexImage data into the native texture allocated memory
            try
            {
                for (uint i = 0; i < imageFaceCount; ++i)
                {
                    for (uint j = 0; j < imageArraySize; ++j)
                    {
                        for (uint k = 0; k < image.MipmapCount; ++k)
                        {
                            Tools.CopyMemory(libraryData.Texture.GetDataPtr(k, j, i), image.SubImageArray[imageCount].Data, image.SubImageArray[imageCount].DataSize * depth);
                            imageCount += depth;

                            depth = depth > 1 ? depth >>= 1 : depth;
                        }
                    }
                }
            }
            catch (AccessViolationException e)
            {
                libraryData.Texture.Dispose();
                Log.Error("Failed to convert texture to PvrTexLib native data, check your texture settings. ", e);
                throw new TextureToolsException("Failed to convert texture to PvrTexLib native data, check your texture settings. ", e);
            }

            // Freeing previous image data
            if (image.DisposingLibrary != null) image.DisposingLibrary.Dispose(image);

            image.LibraryData[this] = libraryData;

            image.DisposingLibrary = this;
        }


        public void EndLibrary(TexImage image)
        {
            // Retrieving native Data.
            if (!image.LibraryData.ContainsKey(this)) return;
            PvrTextureLibraryData libraryData = (PvrTextureLibraryData)image.LibraryData[this];

            // Updating current instance of TexImage with the native Data
            UpdateImage(image, libraryData);

            /*
             * in a 3D texture, the number of sub images will be different than for 2D : with 2D texture, you just have to multiply the mipmap levels with the array size.
             * For 3D, when generating mip map, you generate mip maps for each slice of your texture, but the depth is decreasing by half (like the width and height) at
             * each level. Then the number of sub images won't be mipmapCount * arraySize * depth, but arraySize * ( depth + depth/2 + depth/4 ... )
             *                                                                                                       ---- at each mipmap level ----
             */
            int imageCount, depth;
            if (image.Dimension == TexImage.TextureDimension.Texture3D) // Counting the number of sub images according to the texture dimension
            {
                int subImagePerArrayElementCount = 0;
                int curDepth = image.Depth;
                for (int i = 0; i < image.MipmapCount; ++i)
                {
                    subImagePerArrayElementCount += curDepth;
                    curDepth = curDepth > 1 ? curDepth >>= 1 : curDepth;
                }

                imageCount = (int)(image.ArraySize * image.FaceCount * subImagePerArrayElementCount); // PvrTexLib added a "face" count above the texture array..
            }
            else
            {
                imageCount = (int)(image.ArraySize * image.FaceCount * image.MipmapCount);
            }

            image.SubImageArray = new TexImage.SubImage[imageCount];
            int ct = 0;
            int rowPitch, slicePitch, height, width; 

            for (uint i = 0; i < image.FaceCount; ++i) // Recreating the sub images
            {
                for (uint j = 0; j < image.ArraySize; ++j)
                {
                    depth = image.Depth;
                    for (uint k = 0; k < image.MipmapCount; ++k)
                    {
                        width = (int)libraryData.Header.GetWidth(k);
                        height = (int)libraryData.Header.GetHeight(k);
                        Tools.ComputePitch(image.Format, width, height, out rowPitch, out slicePitch);

                        for (int l = 0; l < depth; ++l)
                        {
                            image.SubImageArray[ct] = new TexImage.SubImage();
                            image.SubImageArray[ct].Width = width;
                            image.SubImageArray[ct].Height = height;
                            image.SubImageArray[ct].RowPitch = rowPitch;
                            image.SubImageArray[ct].SlicePitch = slicePitch;
                            image.SubImageArray[ct].DataSize = slicePitch;
                            image.SubImageArray[ct].Data = new IntPtr(libraryData.Texture.GetDataPtr(k, j, i).ToInt64() + l*slicePitch);
                            ++ct;
                        }
                        depth = depth > 1 ? depth >>= 1 : depth;
                    }
                }
            }

            // PVRTT uses a "face count" for CubeMap texture, the other librairies doesn't, it's just included in the arraySize
            image.ArraySize = image.ArraySize * image.FaceCount;

            // This library is now the "owner" of the TexImage data, and must be used to handle the image data memory
            image.DisposingLibrary = this;
        }


        public void Execute(TexImage image, IRequest request)
        {
            PvrTextureLibraryData libraryData = image.LibraryData.ContainsKey(this) ? (PvrTextureLibraryData)image.LibraryData[this] : null;

            switch (request.Type)
            {
                case RequestType.Loading:
                    Load(image, (LoadingRequest)request);
                    break;

                case RequestType.Compressing:
                    Compress(image, libraryData, (CompressingRequest)request);
                    break;

                case RequestType.Export:
                    Export(image, libraryData, (ExportRequest)request);
                    break;

                case RequestType.Decompressing:
                    Decompress(image, libraryData);
                    break;

                case RequestType.MipMapsGeneration:
                    GenerateMipMaps(image, libraryData, (MipMapsGenerationRequest)request);
                    break;

                case RequestType.SwitchingChannels:
                    SwitchChannels(image, libraryData, (SwitchingBRChannelsRequest)request);
                    break;

                case RequestType.Rescaling:
                    Rescale(image, libraryData, (RescalingRequest)request);
                    break;

                case RequestType.Flipping:
                    Flip(image, libraryData, (FlippingRequest)request);
                    break;

                case RequestType.NormalMapGeneration:
                    GenerateNormalMap(image, libraryData, (NormalMapGenerationRequest)request);
                    break;

                case RequestType.PreMultiplyAlpha:
                    PreMultiplyAlpha(image, libraryData);
                    break;

                default:
                    Log.Error("FITexLib (FreeImage) can't handle this request: " + request.Type);
                    throw new TextureToolsException("FITexLib (FreeImage) can't handle this request: " + request.Type);
            }
        }


        /// <summary>
        /// Loads the specified image.
        /// </summary>
        /// <param name="image">The image.</param>
        /// <param name="request">The request.</param>
        private void Load(TexImage image, LoadingRequest request)
        {
            Log.Info("Loading " + request.FilePath + " ...");

            PvrTextureLibraryData libraryData = new PvrTextureLibraryData();
            image.LibraryData[this] = libraryData;

            libraryData.Texture = new PVRTexture(request.FilePath);
            libraryData.Header = libraryData.Texture.GetHeader();

            image.Width = (int)libraryData.Header.GetWidth();
            image.Height = (int)libraryData.Header.GetHeight();
            image.Depth = (int)libraryData.Header.GetDepth();
            image.Format = RetrieveFormatFromNativeData(libraryData.Header);

            int pitch, slice;
            Tools.ComputePitch(image.Format, (int)image.Width, (int)image.Height, out pitch, out slice);
            image.RowPitch = pitch;
            image.SlicePitch = slice;

            image.DisposingLibrary = this;

            UpdateImage(image, libraryData);

            if (image.FaceCount > 1 && image.FaceCount % 6 == 0)
                image.Dimension = TexImage.TextureDimension.TextureCube;
            else if(image.Depth > 1)
                image.Dimension = TexImage.TextureDimension.Texture3D;
            else if (image.Height > 0)
                image.Dimension = TexImage.TextureDimension.Texture2D;
            else
                image.Dimension = TexImage.TextureDimension.Texture1D;

        }


        /// <summary>
        /// Rescales the specified image.
        /// </summary>
        /// <param name="image">The image.</param>
        /// <param name="libraryData">The library data.</param>
        /// <param name="request">The request.</param>
        private void Rescale(TexImage image, PvrTextureLibraryData libraryData, RescalingRequest request)
        {
            int width = request.ComputeWidth(image);
            int height = request.ComputeHeight(image);

            Log.Info("Rescaling to " + width + "x" + height + " ...");

            EResizeMode filter;
            switch(request.Filter)
            {
                case Filter.Rescaling.Bilinear:
                    filter = EResizeMode.eResizeLinear;
                    break;
                case Filter.Rescaling.Bicubic:
                    filter = EResizeMode.eResizeCubic;
                    break;
                case Filter.Rescaling.Nearest:
                    filter = EResizeMode.eResizeNearest;
                    break;
                default:
                    filter = EResizeMode.eResizeCubic;
                    break;
            }

            Utilities.Resize(libraryData.Texture, (uint)width, (uint)height, (uint)image.Depth, filter);
            UpdateImage(image, libraryData);

            // Updating image data
            image.Rescale(width, height);
        }


        /// <summary>
        /// Exports the specified image.
        /// </summary>
        /// <param name="image">The image.</param>
        /// <param name="libraryData">The library data.</param>
        /// <param name="export">The export request.</param>
        private void Export(TexImage image, PvrTextureLibraryData libraryData, ExportRequest request)
        {
            Log.Info("Exporting to " + request.FilePath + " ...");

            if (request.MinimumMipMapSize > 1) // if a mimimun mipmap size was requested
            {
                int newMipMapCount = image.MipmapCount;
                for (int i = image.MipmapCount - 1; i > 0; --i) // looking for the mipmap level corresponding to the minimum size requeted.
                {
                    if (libraryData.Header.GetWidth((uint)i) >= request.MinimumMipMapSize || libraryData.Header.GetHeight((uint)i) >= request.MinimumMipMapSize)
                    {
                        break;
                    }
                    --newMipMapCount;
                }

                // Creating a new texture corresponding to the requested mipmap levels
                PVRTextureHeader header = new PVRTextureHeader(RetrieveNativeFormat(image.Format), image.Height, image.Width, image.Depth, newMipMapCount, image.ArraySize, image.FaceCount);
                PVRTexture texture = new PVRTexture(header, IntPtr.Zero);

                try
                {
                    for (uint i = 0; i < image.FaceCount; ++i)
                    {
                        for (uint j = 0; j < image.ArraySize; ++j)
                        {
                            for (uint k = 0; k < newMipMapCount; ++k)
                            {
                                Tools.CopyMemory(texture.GetDataPtr(k, j, i), libraryData.Texture.GetDataPtr(k, j, i), (int)libraryData.Header.GetDataSize((int)k, false, false));
                            }
                        }
                    }
                }
                catch (AccessViolationException e)
                {
                    texture.Dispose();
                    Log.Error("Failed to export texture with the mipmap minimum size request. ", e);
                    throw new TextureToolsException("Failed to export texture with the mipmap minimum size request. ", e);
                }

                // Saving the texture into a file and deleting it
                texture.Save(request.FilePath);
                texture.Dispose();
            }
            else
            {
                libraryData.Texture.Save(request.FilePath);
            }

            image.Save(request.FilePath);
        }


        /// <summary>
        /// Switches the channels R and B.
        /// </summary>
        /// <param name="image">The image.</param>
        /// <param name="libraryData">The library data.</param>
        /// <param name="request">The request.</param>
        /// <exception cref="TexLibraryException">Unsuported format for channel switching.</exception>
        private void SwitchChannels(TexImage image, PvrTextureLibraryData libraryData, SwitchingBRChannelsRequest request)
        {
            Log.Info("Switching channels B and R ...");

            switch (image.Format)
            {
                case SiliconStudio.Paradox.Graphics.PixelFormat.B8G8R8A8_UNorm:
                    image.Format = SiliconStudio.Paradox.Graphics.PixelFormat.R8G8B8A8_UNorm; break;
                case SiliconStudio.Paradox.Graphics.PixelFormat.B8G8R8A8_Typeless:
                    image.Format = SiliconStudio.Paradox.Graphics.PixelFormat.R8G8B8A8_Typeless; break;
                case SiliconStudio.Paradox.Graphics.PixelFormat.B8G8R8A8_UNorm_SRgb:
                    image.Format = SiliconStudio.Paradox.Graphics.PixelFormat.R8G8B8A8_UNorm_SRgb; break;
                case SiliconStudio.Paradox.Graphics.PixelFormat.R8G8B8A8_Typeless:
                    image.Format = SiliconStudio.Paradox.Graphics.PixelFormat.B8G8R8A8_Typeless; break;
                case SiliconStudio.Paradox.Graphics.PixelFormat.R8G8B8A8_UNorm:
                    image.Format = SiliconStudio.Paradox.Graphics.PixelFormat.B8G8R8A8_UNorm; break;
                case SiliconStudio.Paradox.Graphics.PixelFormat.R8G8B8A8_UNorm_SRgb:
                    image.Format = SiliconStudio.Paradox.Graphics.PixelFormat.B8G8R8A8_UNorm_SRgb; break;
                default:
                    Log.Error("Unsuported format for channel switching.");
                    throw new TextureToolsException("Unsuported format for channel switching.");
            }

            PVRTexture textureTemp = new PVRTexture(libraryData.Header, libraryData.Texture.GetDataPtr());

            EChannelName e1 = EChannelName.eBlue;
            EChannelName e2 = EChannelName.eRed;

            Utilities.CopyChannels(libraryData.Texture, textureTemp, 1, out e1, out e2);
            Utilities.CopyChannels(libraryData.Texture, textureTemp, 1, out e2, out e1);

            textureTemp.Dispose();

            UpdateImage(image, libraryData);
        }


        /// <summary>
        /// Compresses the specified image.
        /// </summary>
        /// <param name="image">The image.</param>
        /// <param name="libraryData">The library data.</param>
        /// <param name="compress">The compress.</param>
        /// <exception cref="TexLibraryException">Compression failed</exception>
        private void Compress(TexImage image, PvrTextureLibraryData libraryData, CompressingRequest compress)
        {
            Log.Info("Compressing to " + compress.Format + " ...");

            ulong format = RetrieveNativeFormat(compress.Format);
            EPVRTColourSpace colorSpace = RetrieveNativeColorSpace(compress.Format);
            EPVRTVariableType pixelType = RetrieveNativePixelType(compress.Format);

            lock (lockObject)
            {
                if (!Utilities.Transcode(libraryData.Texture, format, pixelType, colorSpace, (ECompressorQuality)compress.Quality, false))
                {
                    Log.Error("Compression failed!");
                    throw new TextureToolsException("Compression failed!");
                }
            }

            image.Format = compress.Format;
            int pitch, slice;
            Tools.ComputePitch(image.Format, image.Width, image.Height, out pitch, out slice);
            image.RowPitch = pitch;
            image.SlicePitch = slice;

            UpdateImage(image, libraryData);
        }


        /// <summary>
        /// Decompresses the specified image.
        /// </summary>
        /// <param name="image">The image.</param>
        /// <param name="libraryData">The library data.</param>
        /// <exception cref="TexLibraryException">Decompression failed!</exception>
        public void Decompress(TexImage image, PvrTextureLibraryData libraryData)
        {
            Log.Info("Decompressing texture ...");

            if (!Utilities.Transcode(libraryData.Texture, PixelType.Standard8PixelType, libraryData.Header.GetChannelType(), libraryData.Header.GetColourSpace(), ECompressorQuality.ePVRTCNormal, true))
            {
                Log.Error("Decompression failed!");
                throw new TextureToolsException("Decompression failed!");
            }

            image.Format = SiliconStudio.Paradox.Graphics.PixelFormat.R8G8B8A8_UNorm;

            int pitch,slice;
            Tools.ComputePitch(image.Format, image.Width, image.Height, out pitch, out slice);
            image.RowPitch = pitch;
            image.SlicePitch = slice;
 
            UpdateImage(image, libraryData);
        }


        /// <summary>
        /// Generates the mip maps.
        /// </summary>
        /// <param name="image">The image.</param>
        /// <param name="libraryData">The library data.</param>
        /// <param name="request">The request.</param>
        private void GenerateMipMaps(TexImage image, PvrTextureLibraryData libraryData, MipMapsGenerationRequest request)
        {
            Log.Info("Generating Mipmaps ... ");

            EResizeMode filter;
            switch (request.Filter)
            {
                case Filter.MipMapGeneration.Linear:
                    filter = EResizeMode.eResizeLinear;
                    break;
                case Filter.MipMapGeneration.Cubic:
                    filter = EResizeMode.eResizeCubic;
                    break;
                case Filter.MipMapGeneration.Nearest:
                    filter = EResizeMode.eResizeNearest;
                    break;
                default:
                    filter = EResizeMode.eResizeCubic;
                    break;
            }

            libraryData.Texture.GenerateMIPMaps(filter);

            UpdateImage(image, libraryData);
        }


        /// <summary>
        /// Flips the specified image vertically or horizontally.
        /// </summary>
        /// <param name="image">The image.</param>
        /// <param name="libraryData">The library data.</param>
        /// <param name="request">The request.</param>
        /// <exception cref="TexLibraryException">Flipping failed.</exception>
        private void Flip(TexImage image, PvrTextureLibraryData libraryData, FlippingRequest request)
        {
            Log.Info("Flipping texture : " + request.Flip + " ... ");

            switch(request.Flip)
            {
                case Orientation.Horizontal:
                    if (!Utilities.Flip(libraryData.Texture, EPVRTAxis.ePVRTAxisX))
                    {
                        Log.Error("Flipping failed.");
                        throw new TextureToolsException("Flipping failed.");
                    }
                    break;
                case Orientation.Vertical:
                    if (!Utilities.Flip(libraryData.Texture, EPVRTAxis.ePVRTAxisY))
                    {
                        Log.Error("Flipping failed.");
                        throw new TextureToolsException("Flipping failed.");
                    }
                    break;
            }

            image.Flip(request.Flip);

            UpdateImage(image, libraryData);
        }

        /// <summary>
        /// Generates the normal map.
        /// </summary>
        /// <param name="image">The image.</param>
        /// <param name="libraryData">The library data.</param>
        /// <param name="request">The request.</param>
        /// <exception cref="TexLibraryException">Failed to generate normal map.</exception>
        public void GenerateNormalMap(TexImage image, PvrTextureLibraryData libraryData, NormalMapGenerationRequest request)
        {
            Log.Info("Generating Normal Map ... ");

            // Creating new TexImage with the normal map data.
            request.NormalMap = new TexImage();
            PvrTextureLibraryData normalMapLibraryData = new PvrTextureLibraryData();
            request.NormalMap.LibraryData[this] = normalMapLibraryData;

            normalMapLibraryData.Texture = new PVRTexture(libraryData.Header, libraryData.Texture.GetDataPtr());
            request.NormalMap.Format = SiliconStudio.Paradox.Graphics.PixelFormat.R8G8B8A8_UNorm;
            request.NormalMap.CurrentLibrary = this;
            request.NormalMap.DisposingLibrary = this;

            if (!Utilities.GenerateNormalMap(normalMapLibraryData.Texture, request.Amplitude, "xyzh"))
            {
                Log.Error("Failed to generate normal map.");
                throw new TextureToolsException("Failed to generate normal map.");
            }

            UpdateImage(request.NormalMap, normalMapLibraryData);
            EndLibrary(request.NormalMap);
            request.NormalMap.DisposingLibrary = this;
        }


        /// <summary>
        /// Premultiplies the alpha on the specified texture.
        /// </summary>
        /// <param name="image">The image.</param>
        /// <param name="libraryData">The library data.</param>
        /// <exception cref="TexLibraryException">Failed to premultiply the alpha.</exception>
        public void PreMultiplyAlpha(TexImage image, PvrTextureLibraryData libraryData)
        {
            Log.Info("Premultiplying alpha ... ");

            if (!Utilities.PreMultipliedAlpha(libraryData.Texture))
            {
                Log.Info("Failed to premultiply the alpha.");
                throw new TextureToolsException("Failed to premultiply the alpha.");
            }
        }
        

        /// <summary>
        /// Updates the image basic information with the native data.
        /// </summary>
        /// <param name="image">The image.</param>
        /// <param name="libraryData">The library data.</param>
        private void UpdateImage(TexImage image, PvrTextureLibraryData libraryData)
        {
            image.Data = libraryData.Texture.GetDataPtr();
            libraryData.Header = libraryData.Texture.GetHeader();
            image.DataSize = (int)libraryData.Header.GetDataSize();
            image.MipmapCount = (int)libraryData.Header.GetNumMIPLevels();
            image.ArraySize = (int)libraryData.Header.GetNumArrayMembers();
            image.FaceCount = (int)libraryData.Header.GetNumFaces();
        }


        /// <summary>
        /// Determines whether the specified compression format is supported by this library
        /// </summary>
        /// <param name="format">The format.</param>
        /// <returns>
        ///     <c>true</c> if the formats is supported by this library; otherwise, <c>false</c>.
        /// </returns>
        private bool SupportFormat(SiliconStudio.Paradox.Graphics.PixelFormat format)
        {
            switch (format)
            {
                case SiliconStudio.Paradox.Graphics.PixelFormat.R32G32B32A32_Float:
                case SiliconStudio.Paradox.Graphics.PixelFormat.R32G32B32_Float:
                case SiliconStudio.Paradox.Graphics.PixelFormat.R32G32B32A32_UInt:
                case SiliconStudio.Paradox.Graphics.PixelFormat.R32G32B32_UInt:
                case SiliconStudio.Paradox.Graphics.PixelFormat.R32G32B32A32_SInt:
                case SiliconStudio.Paradox.Graphics.PixelFormat.R32G32B32_SInt:
                case SiliconStudio.Paradox.Graphics.PixelFormat.R16G16B16A16_UNorm:
                case SiliconStudio.Paradox.Graphics.PixelFormat.R16G16B16A16_UInt:
                case SiliconStudio.Paradox.Graphics.PixelFormat.R16G16B16A16_SNorm:
                case SiliconStudio.Paradox.Graphics.PixelFormat.R16G16B16A16_SInt:
                case SiliconStudio.Paradox.Graphics.PixelFormat.R8G8B8A8_UNorm_SRgb:
                case SiliconStudio.Paradox.Graphics.PixelFormat.R8G8B8A8_UNorm:
                case SiliconStudio.Paradox.Graphics.PixelFormat.R8G8B8A8_UInt:
                case SiliconStudio.Paradox.Graphics.PixelFormat.R8G8B8A8_SNorm:
                case SiliconStudio.Paradox.Graphics.PixelFormat.R8G8B8A8_SInt:
                case SiliconStudio.Paradox.Graphics.PixelFormat.B8G8R8A8_UNorm_SRgb:
                case SiliconStudio.Paradox.Graphics.PixelFormat.B8G8R8A8_UNorm:

                case SiliconStudio.Paradox.Graphics.PixelFormat.PVRTC_2bpp_RGB:
                case SiliconStudio.Paradox.Graphics.PixelFormat.PVRTC_2bpp_RGBA:
                case SiliconStudio.Paradox.Graphics.PixelFormat.PVRTC_4bpp_RGB:
                case SiliconStudio.Paradox.Graphics.PixelFormat.PVRTC_4bpp_RGBA:
                case SiliconStudio.Paradox.Graphics.PixelFormat.PVRTC_II_2bpp:
                case SiliconStudio.Paradox.Graphics.PixelFormat.PVRTC_II_4bpp:
                case SiliconStudio.Paradox.Graphics.PixelFormat.ETC1:
                case SiliconStudio.Paradox.Graphics.PixelFormat.ETC2_RGB:
                case SiliconStudio.Paradox.Graphics.PixelFormat.ETC2_RGBA:
                case SiliconStudio.Paradox.Graphics.PixelFormat.ETC2_RGB_A1:
                case SiliconStudio.Paradox.Graphics.PixelFormat.EAC_R11_Unsigned:
                case SiliconStudio.Paradox.Graphics.PixelFormat.EAC_R11_Signed:
                case SiliconStudio.Paradox.Graphics.PixelFormat.EAC_RG11_Unsigned:
                case SiliconStudio.Paradox.Graphics.PixelFormat.EAC_RG11_Signed:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Retrieves the native format from the PixelFormat.
        /// </summary>
        /// <param name="format">The format.</param>
        /// <returns></returns>
        /// <exception cref="TexLibraryException">UnHandled compression format by PowerVC Texture Tool.</exception>
        private UInt64 RetrieveNativeFormat(SiliconStudio.Paradox.Graphics.PixelFormat format)
        {
            switch (format)
            {
                case SiliconStudio.Paradox.Graphics.PixelFormat.PVRTC_2bpp_RGB:
                    return 0;
                case SiliconStudio.Paradox.Graphics.PixelFormat.PVRTC_2bpp_RGBA:
                    return 1;
                case SiliconStudio.Paradox.Graphics.PixelFormat.PVRTC_4bpp_RGB:
                    return 2;
                case SiliconStudio.Paradox.Graphics.PixelFormat.PVRTC_4bpp_RGBA:
                    return 3;
                case SiliconStudio.Paradox.Graphics.PixelFormat.PVRTC_II_2bpp:
                    return 4;
                case SiliconStudio.Paradox.Graphics.PixelFormat.PVRTC_II_4bpp:
                    return 5;
                case SiliconStudio.Paradox.Graphics.PixelFormat.ETC1:
                    return 6;
                case SiliconStudio.Paradox.Graphics.PixelFormat.ETC2_RGB:
                    return 22;
                case SiliconStudio.Paradox.Graphics.PixelFormat.ETC2_RGBA:
                    return 23;
                case SiliconStudio.Paradox.Graphics.PixelFormat.ETC2_RGB_A1:
                    return 24;
                case SiliconStudio.Paradox.Graphics.PixelFormat.EAC_R11_Unsigned:
                    return 25;
                case SiliconStudio.Paradox.Graphics.PixelFormat.EAC_R11_Signed:
                    return 26;
                case SiliconStudio.Paradox.Graphics.PixelFormat.EAC_RG11_Unsigned:
                    return 27;
                case SiliconStudio.Paradox.Graphics.PixelFormat.EAC_RG11_Signed:
                    return 28;
                case SiliconStudio.Paradox.Graphics.PixelFormat.R32G32B32A32_Float:
                case SiliconStudio.Paradox.Graphics.PixelFormat.R32G32B32_Float:
                case SiliconStudio.Paradox.Graphics.PixelFormat.R32G32B32A32_UInt:
                case SiliconStudio.Paradox.Graphics.PixelFormat.R32G32B32_UInt:
                case SiliconStudio.Paradox.Graphics.PixelFormat.R32G32B32A32_SInt:
                case SiliconStudio.Paradox.Graphics.PixelFormat.R32G32B32_SInt:
                    return Utilities.ConvertPixelType(PixelType.Standard32PixelType);
                case SiliconStudio.Paradox.Graphics.PixelFormat.R16G16B16A16_UNorm:
                case SiliconStudio.Paradox.Graphics.PixelFormat.R16G16B16A16_UInt:
                case SiliconStudio.Paradox.Graphics.PixelFormat.R16G16B16A16_SNorm:
                case SiliconStudio.Paradox.Graphics.PixelFormat.R16G16B16A16_SInt:
                    return Utilities.ConvertPixelType(PixelType.Standard16PixelType);
                case SiliconStudio.Paradox.Graphics.PixelFormat.R8G8B8A8_UNorm_SRgb:
                case SiliconStudio.Paradox.Graphics.PixelFormat.R8G8B8A8_UNorm:
                case SiliconStudio.Paradox.Graphics.PixelFormat.R8G8B8A8_UInt:
                case SiliconStudio.Paradox.Graphics.PixelFormat.R8G8B8A8_SNorm:
                case SiliconStudio.Paradox.Graphics.PixelFormat.R8G8B8A8_SInt:
                    return Utilities.ConvertPixelType(PixelType.Standard8PixelType);
                default:
                    Log.Error("UnHandled compression format by PowerVC Texture Tool.");
                    throw new TextureToolsException("UnHandled compression format by PowerVC Texture Tool.");
            }
        }


        private EPVRTVariableType RetrieveNativePixelType(SiliconStudio.Paradox.Graphics.PixelFormat format)
        {
            switch (format)
            {
                case SiliconStudio.Paradox.Graphics.PixelFormat.R32G32B32A32_Float:
                case SiliconStudio.Paradox.Graphics.PixelFormat.R32G32B32_Float:
                    return EPVRTVariableType.ePVRTVarTypeFloat;
                //case Paradox.Framework.Graphics.PixelFormat.R16G16B16A16_Float:

                case SiliconStudio.Paradox.Graphics.PixelFormat.R32G32B32A32_UInt:
                case SiliconStudio.Paradox.Graphics.PixelFormat.R32G32B32_UInt:
                    return EPVRTVariableType.ePVRTVarTypeUnsignedInteger;
                case SiliconStudio.Paradox.Graphics.PixelFormat.R32G32B32A32_SInt:
                case SiliconStudio.Paradox.Graphics.PixelFormat.R32G32B32_SInt:
                    return EPVRTVariableType.ePVRTVarTypeSignedInteger;

                case SiliconStudio.Paradox.Graphics.PixelFormat.R16G16B16A16_UNorm:
                    return EPVRTVariableType.ePVRTVarTypeUnsignedShortNorm;
                case SiliconStudio.Paradox.Graphics.PixelFormat.R16G16B16A16_UInt:
                    return EPVRTVariableType.ePVRTVarTypeUnsignedShort;
                case SiliconStudio.Paradox.Graphics.PixelFormat.R16G16B16A16_SNorm:
                    return EPVRTVariableType.ePVRTVarTypeSignedShortNorm;
                case SiliconStudio.Paradox.Graphics.PixelFormat.R16G16B16A16_SInt:
                    return EPVRTVariableType.ePVRTVarTypeSignedShort;


                case SiliconStudio.Paradox.Graphics.PixelFormat.R8G8B8A8_UNorm_SRgb:
                case SiliconStudio.Paradox.Graphics.PixelFormat.R8G8B8A8_UNorm:
                    return EPVRTVariableType.ePVRTVarTypeUnsignedByteNorm;
                case SiliconStudio.Paradox.Graphics.PixelFormat.R8G8B8A8_UInt:
                    return EPVRTVariableType.ePVRTVarTypeUnsignedByte;
                case SiliconStudio.Paradox.Graphics.PixelFormat.R8G8B8A8_SNorm:
                    return EPVRTVariableType.ePVRTVarTypeSignedByteNorm;
                case SiliconStudio.Paradox.Graphics.PixelFormat.R8G8B8A8_SInt:
                    return EPVRTVariableType.ePVRTVarTypeSignedByte;

                default:
                    return EPVRTVariableType.ePVRTVarTypeUnsignedByteNorm;
            }
        }


        private SiliconStudio.Paradox.Graphics.PixelFormat RetrieveFormatFromNativeData(PVRTextureHeader header)
        {
            SiliconStudio.Paradox.Graphics.PixelFormat format = header.GetFormat();
            if (format == SiliconStudio.Paradox.Graphics.PixelFormat.R32G32B32A32_Float)
            {
                switch (header.GetChannelType())
                {
                    case EPVRTVariableType.ePVRTVarTypeFloat:
                        return SiliconStudio.Paradox.Graphics.PixelFormat.R32G32B32A32_Float;
                    case EPVRTVariableType.ePVRTVarTypeUnsignedInteger:
                        return SiliconStudio.Paradox.Graphics.PixelFormat.R32G32B32A32_UInt;
                    case EPVRTVariableType.ePVRTVarTypeSignedInteger:
                        return SiliconStudio.Paradox.Graphics.PixelFormat.R32G32B32A32_SInt;
                }
            }
            else if(format == SiliconStudio.Paradox.Graphics.PixelFormat.R16G16B16A16_UNorm)
            {
                switch (header.GetChannelType())
                {
                    case EPVRTVariableType.ePVRTVarTypeUnsignedShortNorm:
                        return SiliconStudio.Paradox.Graphics.PixelFormat.R16G16B16A16_UNorm;
                    case EPVRTVariableType.ePVRTVarTypeUnsignedShort:
                        return SiliconStudio.Paradox.Graphics.PixelFormat.R16G16B16A16_UInt;
                    case EPVRTVariableType.ePVRTVarTypeSignedShortNorm:
                        return SiliconStudio.Paradox.Graphics.PixelFormat.R16G16B16A16_SNorm;
                    case EPVRTVariableType.ePVRTVarTypeSignedShort:
                        return SiliconStudio.Paradox.Graphics.PixelFormat.R16G16B16A16_SInt;
                }
            }
            else if(format == SiliconStudio.Paradox.Graphics.PixelFormat.R8G8B8A8_UNorm)
            {
                switch (header.GetChannelType())
                {
                    case EPVRTVariableType.ePVRTVarTypeUnsignedByteNorm:
                        {
                            if (header.GetColourSpace() == EPVRTColourSpace.ePVRTCSpacelRGB)
                                return SiliconStudio.Paradox.Graphics.PixelFormat.R8G8B8A8_UNorm;
                            else
                                return SiliconStudio.Paradox.Graphics.PixelFormat.R8G8B8A8_UNorm_SRgb;
                        }
                    case EPVRTVariableType.ePVRTVarTypeUnsignedByte:
                        return SiliconStudio.Paradox.Graphics.PixelFormat.R8G8B8A8_UInt;
                    case EPVRTVariableType.ePVRTVarTypeSignedByteNorm:
                        return SiliconStudio.Paradox.Graphics.PixelFormat.R8G8B8A8_SNorm;
                    case EPVRTVariableType.ePVRTVarTypeSignedByte:
                        return SiliconStudio.Paradox.Graphics.PixelFormat.R8G8B8A8_SInt;
                }
            }

            return format;
        }

        private EPVRTColourSpace RetrieveNativeColorSpace(SiliconStudio.Paradox.Graphics.PixelFormat format)
        {
            switch (format)
            {
                case SiliconStudio.Paradox.Graphics.PixelFormat.R8G8B8A8_UNorm_SRgb:
                    return EPVRTColourSpace.ePVRTCSpaceSRgb;
                default:
                    return EPVRTColourSpace.ePVRTCSpacelRGB;
            }
        }
    }
}