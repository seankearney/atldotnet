﻿using Commons;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using static ATL.AudioData.FileStructureHelper;

namespace ATL.AudioData.IO
{
    /// <summary>
    /// Helper class called to write into files, optimizing memory and I/O speed according to the rewritten areas
    /// </summary>
    internal class FileSurgeon
    {
        private const long BUFFER_LIMIT = 150 * 1024 * 1024; // 150 MB

        /// <summary>
        /// Modes for zone block modification
        /// </summary>
        public enum WriteMode
        {
            /// <summary>
            /// Replace : existing block is replaced by written data
            /// </summary>
            REPLACE = 0,
            /// <summary>
            /// Overwrite : written data overwrites existing block (non-overwritten parts are kept as is)
            /// </summary>
            OVERWRITE = 1
        }

        /// <summary>
        /// Modes for zone management
        /// NB : ON_DISK mode can be forced client-side by using <see cref="Settings.ForceDiskIO"/>
        /// </summary>
        public enum ZoneManagement
        {
            /// <summary>
            /// Modifications are performed directly on disk; adapted for small files or single zones
            /// </summary>
            ON_DISK = 0,
            /// <summary>
            /// Modifications are performed in a memory buffer, then written on disk in one go
            /// </summary>
            BUFFERED = 1
        }

        /// <summary>
        /// Buffering region
        /// Describes a group of overlapping, contiguous or neighbouring <see cref="FileStructureHelper.Zone"/>s that can be buffered together for I/O optimization
        /// Two Zones stop belonging to the same region if they are distant by more than <see cref="REGION_DISTANCE_THRESHOLD"/>% of the total file size
        /// </summary>
        private sealed class ZoneRegion
        {
            public ZoneRegion(int id)
            {
                if (-1 == id) throw new ArgumentException("-1 is a reserved value that cannot be attributed");
                Id = id;
            }

            /// <summary>
            /// ID of the region
            /// Used for computation purposes only
            /// Must be unique 
            /// Must be different than -1 which is a reserved value for "unbuffered area" used in <see cref="FileStructureHelper"/>
            /// </summary>
            public readonly int Id;
            /// <summary>
            /// True if the region is bufferable; false if not (i.e. non-resizable zones)
            /// </summary>
            public bool IsBufferable = true;
            /// <summary>
            /// Zones belonging to the region
            /// </summary>
            public IList<Zone> Zones = new List<Zone>();

            public long StartOffset => FileSurgeon.getLowestOffset(Zones);

            public long EndOffset => FileSurgeon.getHighestOffset(Zones);

            public long Size => EndOffset - StartOffset;

            public bool IsReadonly
            {
                get => Zones.All(x => x.IsReadonly);
            }

            public override string ToString()
            {
                return "#" + Id + " : " + StartOffset + "->" + EndOffset + "(" + Utils.GetBytesReadable(Size) + ") IsBufferable = " + IsBufferable;
            }
        }

        /// <summary>
        /// % of total stream (~file) size under which two neighbouring Zones can be grouped into the same Region
        /// </summary>
        private static readonly double REGION_DISTANCE_THRESHOLD = 0.2;

        private readonly FileStructureHelper structureHelper;
        private readonly IMetaDataEmbedder embedder;

        private readonly MetaDataIOFactory.TagType implementedTagType;
        private readonly long defaultTagOffset;

        private readonly ProgressManager writeProgress;

        public delegate WriteResult WriteDelegate(Stream w, TagData tag, Zone zone);


        public class WriteResult
        {
            public readonly WriteMode RequiredMode;
            public readonly int WrittenFields;

            public WriteResult(WriteMode requiredMode, int writtenFields)
            {
                RequiredMode = requiredMode;
                WrittenFields = writtenFields;
            }
        }


        public FileSurgeon(
            FileStructureHelper structureHelper,
            IMetaDataEmbedder embedder,
            MetaDataIOFactory.TagType implementedTagType,
            long defaultTagOffset,
            IProgress<float> writeProgress)
        {
            this.structureHelper = structureHelper;
            this.embedder = embedder;
            this.implementedTagType = implementedTagType;
            this.defaultTagOffset = defaultTagOffset;
            if (writeProgress != null) this.writeProgress = new ProgressManager(writeProgress, "FileSurgeon");
        }

        public FileSurgeon(
            FileStructureHelper structureHelper,
            IMetaDataEmbedder embedder,
            MetaDataIOFactory.TagType implementedTagType,
            long defaultTagOffset,
            Action<float> writeProgress)
        {
            this.structureHelper = structureHelper;
            this.embedder = embedder;
            this.implementedTagType = implementedTagType;
            this.defaultTagOffset = defaultTagOffset;
            if (writeProgress != null) this.writeProgress = new ProgressManager(writeProgress, "FileSurgeon");
        }


        public bool RewriteZones(
            Stream w,
            WriteDelegate write,
            ICollection<Zone> zones,
            TagData dataToWrite,
            bool tagExists)
        {
            ZoneManagement mode;
            if (1 == zones.Count || Settings.ForceDiskIO) mode = ZoneManagement.ON_DISK;
            else mode = ZoneManagement.BUFFERED;

            return RewriteZones(w, write, zones, dataToWrite, tagExists, mode == ZoneManagement.BUFFERED);
        }

        public async Task<bool> RewriteZonesAsync(
            Stream w,
            WriteDelegate write,
            ICollection<Zone> zones,
            TagData dataToWrite,
            bool tagExists)
        {
            ZoneManagement mode;
            if (1 == zones.Count || Settings.ForceDiskIO) mode = ZoneManagement.ON_DISK;
            else mode = ZoneManagement.BUFFERED;

            return await RewriteZonesAsync(w, write, zones, dataToWrite, tagExists, mode == ZoneManagement.BUFFERED);
        }

        /// <summary>
        /// Rewrites zones that have to be rewritten
        ///     - Works region after region, buffering them if needed
        ///     - Put each zone into memory and update them using the given WriteDelegate
        ///     - Adjust file size and region headers accordingly
        /// </summary>
        /// <param name="fullScopeWriter">BinaryWriter opened on the data stream (usually, contents of an audio file) to be rewritten</param>
        /// <param name="write">Delegate to the write method of the <see cref="IMetaDataIO"/> to be used to update the data stream</param>
        /// <param name="zones">Zones to rewrite</param>
        /// <param name="dataToWrite">Metadata to update the zones with</param>
        /// <param name="tagExists">True if the tag already exists on the current data stream; false if not</param>
        /// <param name="useBuffer">True if I/O has to be buffered. Makes I/O faster but consumes more RAM.</param>
        /// <returns>True if the operation succeeded; false if it something unexpected happened during the processing</returns>
        private bool RewriteZones(
            Stream fullScopeWriter,
            WriteDelegate write,
            ICollection<Zone> zones,
            TagData dataToWrite,
            bool tagExists,
            bool useBuffer)
        {
            long oldTagSize;
            long newTagSize;
            long globalOffsetCorrection;
            long globalCumulativeDelta = 0;
            bool result = true;
            bool isBuffered = false;

            IList<ZoneRegion> zoneRegions = computeZoneRegions(zones, fullScopeWriter.Length);
            Stream writer;

            displayRegions(zoneRegions);

            int regionIndex = 0;
            Action<float> progress = initActionProgress(zoneRegions);
            foreach (ZoneRegion region in zoneRegions)
            {
                long regionCumulativeDelta = 0;
                Logging.LogDelegator.GetLogDelegate()(Logging.Log.LV_DEBUG, "------------ REGION " + regionIndex++);

                int initialBufferSize = (int)Math.Min(region.Size, int.MaxValue);
                MemoryStream buffer = null;
                try
                {
                    if (useBuffer && region.IsBufferable && initialBufferSize < BUFFER_LIMIT)
                    {
                        isBuffered = true;
                        Logging.LogDelegator.GetLogDelegate()(Logging.Log.LV_DEBUG, "Buffering " + Utils.GetBytesReadable(initialBufferSize));
                        buffer = new MemoryStream(initialBufferSize);

                        // Copy file data to buffer
                        if (initialBufferSize > 0)
                        {
                            if (structureHelper != null)
                                fullScopeWriter.Seek(structureHelper.getCorrectedOffset(region.StartOffset, region.Id), SeekOrigin.Begin);
                            else // for classes that don't use FileStructureHelper(FLAC)
                                fullScopeWriter.Seek(region.StartOffset + globalCumulativeDelta, SeekOrigin.Begin);

                            StreamUtils.CopyStream(fullScopeWriter, buffer, initialBufferSize);
                        }

                        writer = buffer;
                        globalOffsetCorrection = region.StartOffset;
                    }
                    else
                    {
                        isBuffered = false;
                        writer = fullScopeWriter;
                        globalOffsetCorrection = 0;
                    }

                    foreach (Zone zone in region.Zones)
                    {
                        bool isNothing;
                        oldTagSize = zone.Size;
                        WriteResult writeResult;

                        Logging.LogDelegator.GetLogDelegate()(Logging.Log.LV_DEBUG, "------ ZONE " + zone.Name + (zone.IsReadonly ? " (read-only) " : "") + "@" + zone.Offset);
                        Logging.LogDelegator.GetLogDelegate()(Logging.Log.LV_DEBUG, "Allocating " + Utils.GetBytesReadable(zone.Size));

                        // Write new tag to a MemoryStream
                        using (MemoryStream memStream = new MemoryStream((int)Math.Min(zone.Size, int.MaxValue)))
                        {
                            if (!zone.IsReadonly)
                            {
                                // DataSizeDelta needs to be incremented to be used by classes that don't use FileStructureHelper (e.g. FLAC)
                                dataToWrite.DataSizeDelta = globalCumulativeDelta;
                                writeResult = write(memStream, dataToWrite, zone);

                                if (WriteMode.REPLACE == writeResult.RequiredMode)
                                {
                                    if (writeResult.WrittenFields > 0)
                                    {
                                        newTagSize = memStream.Length;

                                        if (embedder != null && implementedTagType == MetaDataIOFactory.TagType.ID3V2)
                                        {
                                            // Insert header before the written metadata
                                            if (embedder.ID3v2EmbeddingHeaderSize > 0)
                                            {
                                                StreamUtils.LengthenStream(memStream, 0, embedder.ID3v2EmbeddingHeaderSize);
                                                memStream.Position = 0;
                                                embedder.WriteID3v2EmbeddingHeader(memStream, newTagSize);
                                            }
                                            // Write footer after the written metadata
                                            memStream.Seek(0, SeekOrigin.End);
                                            embedder.WriteID3v2EmbeddingFooter(memStream, newTagSize);

                                            newTagSize = memStream.Length;
                                        }
                                    }
                                    else
                                    {
                                        newTagSize = zone.CoreSignature.Length;
                                    }
                                }
                                else // Overwrite mode
                                {
                                    newTagSize = zone.Size;
                                }
                            }
                            else // Read-only zone
                            {
                                writeResult = new WriteResult(WriteMode.OVERWRITE, 0);
                                newTagSize = oldTagSize;
                            }
                            long delta = newTagSize - oldTagSize;
                            isNothing = 0 == oldTagSize && 0 == delta; // Avoids unnecessary operations to optimize processing time

                            Logging.LogDelegator.GetLogDelegate()(Logging.Log.LV_DEBUG, "newTagSize : " + Utils.GetBytesReadable(newTagSize));

                            // -- Adjust tag slot to new size in file --
                            Tuple<long, long> tagBoundaries = calcTagBoundaries(zone, writer, isBuffered, tagExists, globalCumulativeDelta, regionCumulativeDelta, globalOffsetCorrection);
                            long tagBeginOffset = tagBoundaries.Item1;
                            long tagEndOffset = tagBoundaries.Item2;

                            if (WriteMode.REPLACE == writeResult.RequiredMode && !isNothing)
                            {
                                // Need to build a larger file
                                if (newTagSize > zone.Size)
                                {
                                    uint deltaBytes = (uint)(newTagSize - zone.Size);
                                    if (!useBuffer) Logging.LogDelegator.GetLogDelegate()(Logging.Log.LV_DEBUG, "Disk stream operation (direct) : Lengthening (delta=" + Utils.GetBytesReadable(deltaBytes) + ")");
                                    else Logging.LogDelegator.GetLogDelegate()(Logging.Log.LV_DEBUG, "Buffer stream operation : Lengthening (delta=" + Utils.GetBytesReadable(deltaBytes) + ")");

                                    StreamUtils.LengthenStream(writer, tagEndOffset, deltaBytes, false, (null == buffer) ? progress : null);
                                }
                                else if (newTagSize < zone.Size) // Need to reduce file size
                                {
                                    uint deltaBytes = (uint)(zone.Size - newTagSize);
                                    if (!useBuffer) Logging.LogDelegator.GetLogDelegate()(Logging.Log.LV_DEBUG, "Disk stream operation (direct) : Shortening (delta=-" + Utils.GetBytesReadable(deltaBytes) + ")");
                                    else Logging.LogDelegator.GetLogDelegate()(Logging.Log.LV_DEBUG, "Buffer stream operation : Shortening (delta=-" + Utils.GetBytesReadable(deltaBytes) + ")");

                                    StreamUtils.ShortenStream(writer, tagEndOffset, deltaBytes, (null == buffer) ? progress : null);
                                }
                            }

                            // Copy tag contents to the new slot
                            if (!isNothing)
                            {
                                writer.Seek(tagBeginOffset, SeekOrigin.Begin);
                                memStream.Seek(0, SeekOrigin.Begin);

                                if (writeResult.WrittenFields > 0) StreamUtils.CopyStream(memStream, writer);
                                else if (zone.CoreSignature.Length > 0) memStream.Write(zone.CoreSignature, 0, zone.CoreSignature.Length);
                            }

                            regionCumulativeDelta += delta;
                            globalCumulativeDelta += delta;

                            // Edit wrapping size markers and frame counters if needed
                            ACTION action = detectAction(writeResult, delta, oldTagSize, newTagSize, zone);
                            if (action != ACTION.None)
                            {
                                // Use plain writer here on purpose because its zone contains headers for the zones adressed by the static writer
                                result &= structureHelper.RewriteHeaders(fullScopeWriter, isBuffered ? writer : null, delta, action, zone.Name, globalOffsetCorrection, isBuffered ? region.Id : -1);
                            }

                            zone.Size = (int)newTagSize;
                        } // MemoryStream used to process current zone

                        if (null == buffer && writeProgress != null && !zone.IsReadonly) progress = incrementProgress(progress);
                    } // Loop through zones

                    if (buffer != null)
                    {
                        // -- Adjust file slot to new size of buffer --
                        long tagEndOffset;
                        if (structureHelper != null)
                            tagEndOffset = structureHelper.getCorrectedOffset(region.StartOffset, region.Id) + initialBufferSize;
                        else // for classes that don't use FileStructureHelper(FLAC)
                            tagEndOffset = region.StartOffset + globalCumulativeDelta - regionCumulativeDelta + initialBufferSize;

                        // Need to build a larger file
                        if (buffer.Length > initialBufferSize)
                        {
                            Logging.LogDelegator.GetLogDelegate()(Logging.Log.LV_DEBUG, "Disk stream operation (buffer) : Lengthening (delta=" + Utils.GetBytesReadable(buffer.Length - initialBufferSize) + "; endOffset=" + tagEndOffset + ")");
                            StreamUtils.LengthenStream(fullScopeWriter, tagEndOffset, (uint)(buffer.Length - initialBufferSize), false, progress);
                        }
                        else if (buffer.Length < initialBufferSize) // Need to reduce file size
                        {
                            Logging.LogDelegator.GetLogDelegate()(Logging.Log.LV_DEBUG, "Disk stream operation (buffer) : Shortening (delta=" + Utils.GetBytesReadable(buffer.Length - initialBufferSize) + "; endOffset=" + tagEndOffset + ")");
                            StreamUtils.ShortenStream(fullScopeWriter, tagEndOffset, (uint)(initialBufferSize - buffer.Length), progress);
                        }

                        // Copy tag contents to the new slot
                        if (structureHelper != null)
                            fullScopeWriter.Seek(structureHelper.getCorrectedOffset(region.StartOffset, region.Id), SeekOrigin.Begin);
                        else // for classes that don't use FileStructureHelper(FLAC)
                            fullScopeWriter.Seek(region.StartOffset + globalCumulativeDelta - regionCumulativeDelta, SeekOrigin.Begin); // don't apply self-created delta

                        buffer.Seek(0, SeekOrigin.Begin);

                        StreamUtils.CopyStream(buffer, fullScopeWriter);
                    }

                    // Increment progress section for current region
                    if (buffer != null && writeProgress != null) progress = incrementProgress(progress);
                }
                finally // Make sure buffers are properly disallocated
                {
                    if (buffer != null)
                    {
                        buffer.Close();
                        buffer = null;
                    }
                }

                Logging.LogDelegator.GetLogDelegate()(Logging.Log.LV_DEBUG, "");
            } // Loop through zone regions

            applyPostProcessing(fullScopeWriter);
            Logging.LogDelegator.GetLogDelegate()(Logging.Log.LV_DEBUG, "DONE");

            return result;
        }

        private async Task<bool> RewriteZonesAsync(
            Stream fullScopeWriter,
            WriteDelegate write,
            ICollection<Zone> zones,
            TagData dataToWrite,
            bool tagExists,
            bool useBuffer)
        {
            long oldTagSize;
            long newTagSize;
            long globalOffsetCorrection;
            long globalCumulativeDelta = 0;
            bool result = true;
            bool isBuffered = false;

            IList<ZoneRegion> zoneRegions = computeZoneRegions(zones, fullScopeWriter.Length);
            Stream writer;

            displayRegions(zoneRegions);

            int regionIndex = 0;
            IProgress<float> progress = initIProgress(zoneRegions);
            foreach (ZoneRegion region in zoneRegions)
            {
                long regionCumulativeDelta = 0;
                Logging.LogDelegator.GetLogDelegate()(Logging.Log.LV_DEBUG, "------------ REGION " + regionIndex++);

                int initialBufferSize = (int)Math.Min(region.Size, int.MaxValue);
                MemoryStream buffer = null;
                try
                {
                    if (useBuffer && region.IsBufferable && initialBufferSize < BUFFER_LIMIT)
                    {
                        isBuffered = true;
                        Logging.LogDelegator.GetLogDelegate()(Logging.Log.LV_DEBUG, "Buffering " + Utils.GetBytesReadable(initialBufferSize));
                        buffer = new MemoryStream(initialBufferSize);

                        // Copy file data to buffer
                        if (initialBufferSize > 0)
                        {
                            if (structureHelper != null)
                                fullScopeWriter.Seek(structureHelper.getCorrectedOffset(region.StartOffset, region.Id), SeekOrigin.Begin);
                            else // for classes that don't use FileStructureHelper(FLAC)
                                fullScopeWriter.Seek(region.StartOffset + globalCumulativeDelta, SeekOrigin.Begin);

                            await StreamUtilsAsync.CopyStreamAsync(fullScopeWriter, buffer, initialBufferSize);
                        }

                        writer = buffer;
                        globalOffsetCorrection = region.StartOffset;
                    }
                    else
                    {
                        isBuffered = false;
                        writer = fullScopeWriter;
                        globalOffsetCorrection = 0;
                    }

                    foreach (Zone zone in region.Zones)
                    {
                        bool isNothing;
                        oldTagSize = zone.Size;
                        WriteResult writeResult;

                        Logging.LogDelegator.GetLogDelegate()(Logging.Log.LV_DEBUG, "------ ZONE " + zone.Name + (zone.IsReadonly ? " (read-only) " : "") + "@" + zone.Offset);
                        Logging.LogDelegator.GetLogDelegate()(Logging.Log.LV_DEBUG, "Allocating " + Utils.GetBytesReadable(zone.Size));

                        // Write new tag to a MemoryStream
                        using (MemoryStream memStream = new MemoryStream((int)Math.Min(zone.Size, int.MaxValue)))
                        {
                            if (!zone.IsReadonly)
                            {
                                // DataSizeDelta needs to be incremented to be used by classes that don't use FileStructureHelper (e.g. FLAC)
                                dataToWrite.DataSizeDelta = globalCumulativeDelta;
                                writeResult = write(memStream, dataToWrite, zone);

                                if (WriteMode.REPLACE == writeResult.RequiredMode)
                                {
                                    if (writeResult.WrittenFields > 0)
                                    {
                                        newTagSize = memStream.Length;

                                        if (embedder != null && implementedTagType == MetaDataIOFactory.TagType.ID3V2)
                                        {
                                            // Insert header before the written metadata
                                            if (embedder.ID3v2EmbeddingHeaderSize > 0)
                                            {
                                                StreamUtils.LengthenStream(memStream, 0, embedder.ID3v2EmbeddingHeaderSize);
                                                memStream.Position = 0;
                                                embedder.WriteID3v2EmbeddingHeader(memStream, newTagSize);
                                            }
                                            // Write footer after the written metadata
                                            memStream.Seek(0, SeekOrigin.End);
                                            embedder.WriteID3v2EmbeddingFooter(memStream, newTagSize);

                                            newTagSize = memStream.Length;
                                        }
                                    }
                                    else
                                    {
                                        newTagSize = zone.CoreSignature.Length;
                                    }
                                }
                                else // Overwrite mode
                                {
                                    newTagSize = zone.Size;
                                }
                            }
                            else // Read-only zone
                            {
                                writeResult = new WriteResult(WriteMode.OVERWRITE, 0);
                                newTagSize = oldTagSize;
                            }
                            long delta = newTagSize - oldTagSize;
                            isNothing = 0 == oldTagSize && 0 == delta; // Avoids unnecessary operations to optimize processing time

                            Logging.LogDelegator.GetLogDelegate()(Logging.Log.LV_DEBUG, "newTagSize : " + Utils.GetBytesReadable(newTagSize));

                            // -- Adjust tag slot to new size in file --
                            Tuple<long, long> tagBoundaries = calcTagBoundaries(zone, writer, isBuffered, tagExists, globalCumulativeDelta, regionCumulativeDelta, globalOffsetCorrection);
                            long tagBeginOffset = tagBoundaries.Item1;
                            long tagEndOffset = tagBoundaries.Item2;

                            if (WriteMode.REPLACE == writeResult.RequiredMode && !isNothing)
                            {
                                // Need to build a larger file
                                if (newTagSize > zone.Size)
                                {
                                    uint deltaBytes = (uint)(newTagSize - zone.Size);
                                    if (!useBuffer) Logging.LogDelegator.GetLogDelegate()(Logging.Log.LV_DEBUG, "Disk stream operation (direct) : Lengthening (delta=" + Utils.GetBytesReadable(deltaBytes) + ")");
                                    else Logging.LogDelegator.GetLogDelegate()(Logging.Log.LV_DEBUG, "Buffer stream operation : Lengthening (delta=" + Utils.GetBytesReadable(deltaBytes) + ")");

                                    await StreamUtilsAsync.LengthenStreamAsync(writer, tagEndOffset, deltaBytes, (null == buffer) ? progress : null);
                                }
                                else if (newTagSize < zone.Size) // Need to reduce file size
                                {
                                    uint deltaBytes = (uint)(zone.Size - newTagSize);
                                    if (!useBuffer) Logging.LogDelegator.GetLogDelegate()(Logging.Log.LV_DEBUG, "Disk stream operation (direct) : Shortening (delta=-" + Utils.GetBytesReadable(deltaBytes) + ")");
                                    else Logging.LogDelegator.GetLogDelegate()(Logging.Log.LV_DEBUG, "Buffer stream operation : Shortening (delta=-" + Utils.GetBytesReadable(deltaBytes) + ")");

                                    await StreamUtilsAsync.ShortenStreamAsync(writer, tagEndOffset, deltaBytes, (null == buffer) ? progress : null);
                                }
                            }

                            // Copy tag contents to the new slot
                            if (!isNothing)
                            {
                                writer.Seek(tagBeginOffset, SeekOrigin.Begin);
                                memStream.Seek(0, SeekOrigin.Begin);

                                if (writeResult.WrittenFields > 0) await StreamUtilsAsync.CopyStreamAsync(memStream, writer);
                                else if (zone.CoreSignature.Length > 0) await memStream.WriteAsync(zone.CoreSignature, 0, zone.CoreSignature.Length);
                            }

                            regionCumulativeDelta += delta;
                            globalCumulativeDelta += delta;

                            // Edit wrapping size markers and frame counters if needed
                            ACTION action = detectAction(writeResult, delta, oldTagSize, newTagSize, zone);
                            if (action != ACTION.None)
                            {
                                // Use plain writer here on purpose because its zone contains headers for the zones adressed by the static writer
                                result &= structureHelper.RewriteHeaders(fullScopeWriter, isBuffered ? writer : null, delta, action, zone.Name, globalOffsetCorrection, isBuffered ? region.Id : -1);
                            }

                            zone.Size = (int)newTagSize;
                        } // MemoryStream used to process current zone

                        if (null == buffer && writeProgress != null && !zone.IsReadonly) progress = incrementProgress(progress);
                    } // Loop through zones

                    if (buffer != null)
                    {
                        // -- Adjust file slot to new size of buffer --
                        long tagEndOffset;
                        if (structureHelper != null)
                            tagEndOffset = structureHelper.getCorrectedOffset(region.StartOffset, region.Id) + initialBufferSize;
                        else // for classes that don't use FileStructureHelper(FLAC)
                            tagEndOffset = region.StartOffset + globalCumulativeDelta - regionCumulativeDelta + initialBufferSize;

                        // Need to build a larger file
                        if (buffer.Length > initialBufferSize)
                        {
                            Logging.LogDelegator.GetLogDelegate()(Logging.Log.LV_DEBUG, "Disk stream operation (buffer) : Lengthening (delta=" + Utils.GetBytesReadable(buffer.Length - initialBufferSize) + "; endOffset=" + tagEndOffset + ")");
                            await StreamUtilsAsync.LengthenStreamAsync(fullScopeWriter, tagEndOffset, (uint)(buffer.Length - initialBufferSize), progress);
                        }
                        else if (buffer.Length < initialBufferSize) // Need to reduce file size
                        {
                            Logging.LogDelegator.GetLogDelegate()(Logging.Log.LV_DEBUG, "Disk stream operation (buffer) : Shortening (delta=" + Utils.GetBytesReadable(buffer.Length - initialBufferSize) + "; endOffset=" + tagEndOffset + ")");
                            await StreamUtilsAsync.ShortenStreamAsync(fullScopeWriter, tagEndOffset, (uint)(initialBufferSize - buffer.Length), progress);
                        }

                        // Copy tag contents to the new slot
                        if (structureHelper != null)
                            fullScopeWriter.Seek(structureHelper.getCorrectedOffset(region.StartOffset, region.Id), SeekOrigin.Begin);
                        else // for classes that don't use FileStructureHelper(FLAC)
                            fullScopeWriter.Seek(region.StartOffset + globalCumulativeDelta - regionCumulativeDelta, SeekOrigin.Begin); // don't apply self-created delta

                        buffer.Seek(0, SeekOrigin.Begin);

                        await StreamUtilsAsync.CopyStreamAsync(buffer, fullScopeWriter);
                    }

                    // Increment progress section for current region
                    if (buffer != null && writeProgress != null) progress = incrementProgress(progress);
                }
                finally // Make sure buffers are properly disallocated
                {
                    if (buffer != null)
                    {
                        buffer.Close();
                        buffer = null;
                    }
                }

                Logging.LogDelegator.GetLogDelegate()(Logging.Log.LV_DEBUG, "");
            } // Loop through zone regions

            applyPostProcessing(fullScopeWriter);
            Logging.LogDelegator.GetLogDelegate()(Logging.Log.LV_DEBUG, "DONE");

            return result;
        }

        private void displayRegions(IList<ZoneRegion> zoneRegions)
        {
            Logging.LogDelegator.GetLogDelegate()(Logging.Log.LV_DEBUG, "========================================");
            Logging.LogDelegator.GetLogDelegate()(Logging.Log.LV_DEBUG, "Found " + zoneRegions.Count + " regions");
            foreach (ZoneRegion region in zoneRegions) Logging.LogDelegator.GetLogDelegate()(Logging.Log.LV_DEBUG, region.ToString());
            Logging.LogDelegator.GetLogDelegate()(Logging.Log.LV_DEBUG, "========================================");
        }

        private void initProgressManager(IList<ZoneRegion> zoneRegions)
        {
            int maxCount = 0;
            foreach (ZoneRegion region in zoneRegions)
            {
                if (region.IsBufferable) maxCount++; // If region is buffered, actual file I/O may happen once for the entire region
                else maxCount += region.Zones.Count(z => !z.IsReadonly); // else it may happen once on each Zone, except for read-only zones
            }
            writeProgress.MaxSections = maxCount;
        }

        private IProgress<float> initIProgress(IList<ZoneRegion> zoneRegions)
        {
            if (writeProgress != null)
            {
                initProgressManager(zoneRegions);
                return writeProgress.CreateIProgress();
            }
            return null;
        }

        private Action<float> initActionProgress(IList<ZoneRegion> zoneRegions)
        {
            if (writeProgress != null)
            {
                initProgressManager(zoneRegions);
                return writeProgress.CreateAction();
            }
            return null;
        }

        private IProgress<float> incrementProgress(IProgress<float> progress)
        {
            // Make sure final progress of buffered zone is reported, especially when no resizing has been involved
            progress.Report(1);
            // Increment progress section   
            writeProgress.CurrentSection++;
            return writeProgress.CreateIProgress();
        }

        private Action<float> incrementProgress(Action<float> progress)
        {
            // Make sure final progress of buffered zone is reported, especially when no resizing has been involved
            progress(1);
            // Increment progress section   
            writeProgress.CurrentSection++;
            return writeProgress.CreateAction();
        }

        private Tuple<long, long> calcTagBoundaries(Zone zone, Stream writer, bool isBuffered, bool tagExists, long globalCumulativeDelta, long regionCumulativeDelta, long globalOffsetCorrection)
        {
            long tagBeginOffset, tagEndOffset;
            if (tagExists && zone.Size > zone.CoreSignature.Length) // An existing tag has been reprocessed
            {
                tagBeginOffset = zone.Offset + (isBuffered ? regionCumulativeDelta : globalCumulativeDelta) - globalOffsetCorrection;
                tagEndOffset = tagBeginOffset + zone.Size;
            }
            else // A brand new tag has been added to the file
            {
                if (embedder != null && implementedTagType == MetaDataIOFactory.TagType.ID3V2)
                {
                    tagBeginOffset = embedder.Id3v2Zone.Offset - globalOffsetCorrection;
                }
                else
                {
                    switch (defaultTagOffset)
                    {
                        case MetaDataIO.TO_EOF: tagBeginOffset = writer.Length; break;
                        case MetaDataIO.TO_BOF: tagBeginOffset = 0; break;
                        case MetaDataIO.TO_BUILTIN: tagBeginOffset = zone.Offset + (isBuffered ? regionCumulativeDelta : globalCumulativeDelta); break;
                        default: tagBeginOffset = -1; break;
                    }
                    tagBeginOffset -= globalOffsetCorrection;
                }
                tagEndOffset = tagBeginOffset + zone.Size;
            }
            return new Tuple<long, long>(tagBeginOffset, tagEndOffset);
        }

        private ACTION detectAction(WriteResult writeResult, long delta, long oldTagSize, long newTagSize, Zone zone)
        {
            if (structureHelper != null && (MetaDataIOFactory.TagType.NATIVE == implementedTagType || (embedder != null && implementedTagType == MetaDataIOFactory.TagType.ID3V2)))
            {
                bool isTagWritten = writeResult.WrittenFields > 0;

                if (0 == delta) return ACTION.Edit; // Zone content has not changed; headers might need to be rewritten (e.g. offset changed)
                else
                {
                    if (oldTagSize == zone.CoreSignature.Length && isTagWritten) return ACTION.Add;
                    else if (newTagSize == zone.CoreSignature.Length && !isTagWritten) return ACTION.Delete;
                    else return ACTION.Edit;
                }
            }
            return ACTION.None;
        }

        private void applyPostProcessing(Stream fullScopeWriter)
        {
            // Post-processing changes
            if (structureHelper != null && structureHelper.ZoneNames.Any(z => z.StartsWith(POST_PROCESSING_ZONE_NAME)))
            {
                Logging.LogDelegator.GetLogDelegate()(Logging.Log.LV_DEBUG, "Post-processing");
                structureHelper.PostProcessing(fullScopeWriter);
            }
        }

        /// <summary>
        /// Build buffering Regions according to the given zones and total stream (usually file) size
        /// </summary>
        /// <param name="zones">Zones to calculate Regions from, ordered by their offset</param>
        /// <param name="streamSize">Total size of the corresponding file, in bytes</param>
        /// <returns>Buffering Regions containing the given zones</returns>
        private IList<ZoneRegion> computeZoneRegions(ICollection<Zone> zones, long streamSize)
        {
            IList<ZoneRegion> result = new List<ZoneRegion>();

            bool isFirst = true;
            bool embedderProcessed = false;

            bool previousIsResizable = false;
            long previousZoneEndOffset = -1;
            int regionId = 0;
            ZoneRegion region = new ZoneRegion(regionId++);

            foreach (Zone zone in zones)
            {
                if (isFirst) region.IsBufferable = zone.IsResizable;

                long zoneBeginOffset = getLowestOffset(zone);
                long zoneEndOffset = getHighestOffset(zone);

                if (embedder != null && !embedderProcessed && implementedTagType == MetaDataIOFactory.TagType.ID3V2)
                {
                    zoneBeginOffset = Math.Min(zoneBeginOffset, embedder.Id3v2Zone.Offset);
                    zoneEndOffset = Math.Max(zoneEndOffset, embedder.Id3v2Zone.Offset + embedder.Id3v2Zone.Size);
                    embedderProcessed = true;
                }

                // If current zone is distant to the previous by more than 20% of total file size, create another region
                // If current zone has not the same IsResizable value as the previous, create another region
                if (!isFirst &&
                    (
                        (zone.IsResizable && zoneBeginOffset - previousZoneEndOffset > streamSize * REGION_DISTANCE_THRESHOLD)
                        || (previousIsResizable != zone.IsResizable)
                    )
                    )
                {
                    result.Add(region);
                    region = new ZoneRegion(regionId++);
                    region.IsBufferable = zone.IsResizable;
                }

                previousZoneEndOffset = zoneEndOffset;
                previousIsResizable = zone.IsResizable;
                region.Zones.Add(zone);
                isFirst = false;
            }

            // Finalize current region
            result.Add(region);

            return result.OrderBy(r => r.IsReadonly).ToList();
        }

        /// <summary>
        /// Get the lowest offset among the given zones
        /// Searches through zone offsets _and_ header offsets
        /// </summary>
        /// <param name="zones">Zones to examine</param>
        /// <returns>Lowest offset value among the given zones' zone offsets and header offsets</returns>
        private static long getLowestOffset(ICollection<Zone> zones)
        {
            long result = long.MaxValue;
            if (zones != null)
                foreach (Zone zone in zones)
                    result = Math.Min(result, getLowestOffset(zone));

            return result;
        }

        /// <summary>
        /// Get the lowest offset among the given zone
        /// Searches through zone offsets _and_ header offsets
        /// </summary>
        /// <param name="zone">Zone to examine</param>
        /// <returns>Lowest offset value among the given zone's zone offsets and header offsets</returns>
        private static long getLowestOffset(Zone zone)
        {
            long result = long.MaxValue;
            if (zone != null)
            {
                result = Math.Min(result, zone.Offset);
                foreach (FrameHeader header in zone.Headers)
                    result = Math.Min(result, header.Position);
            }
            return result;
        }

        /// <summary>
        /// Get the highest offset among the given zones
        /// Searches through zone offsets _and_ header offsets
        /// </summary>
        /// <param name="zones">Zones to examine</param>
        /// <returns>Highest offset value among the given zones' zone offsets and header offsets</returns>
        private static long getHighestOffset(ICollection<Zone> zones)
        {
            long result = 0;
            if (zones != null)
                foreach (Zone zone in zones)
                    result = Math.Max(result, getHighestOffset(zone));

            return result;
        }

        /// <summary>
        /// Get the highest offset among the given zone
        /// Searches through zone offsets _and_ header offsets
        /// </summary>
        /// <param name="zone">Zone to examine</param>
        /// <returns>Highest offset value among the given zone's zone offsets and header offsets</returns>
        private static long getHighestOffset(Zone zone)
        {
            long result = 0;
            if (zone != null)
            {
                result = Math.Max(result, zone.Offset + zone.Size);
                foreach (FrameHeader header in zone.Headers)
                    result = Math.Max(result, header.Position);
            }
            return result;
        }
    }
}
