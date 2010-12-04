﻿using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace BTDB
{
    internal sealed class LowLevelDBTransaction : ILowLevelDBTransaction
    {
        readonly LowLevelDB _owner;

        // if this is null then this transaction is writing kind
        LowLevelDB.ReadTrLink _readLink;
        Sector _currentKeySector;
        readonly List<Sector> _currentKeySectorParents = new List<Sector>();
        int _currentKeyIndexInLeaf;
        long _currentKeyIndex;
        byte[] _prefix;
        long _prefixKeyStart;
        long _prefixKeyCount;
        static readonly byte[] EmptyByteArray = new byte[0];

        internal LowLevelDBTransaction(LowLevelDB owner, LowLevelDB.ReadTrLink readLink)
        {
            _owner = owner;
            _readLink = readLink;
            _currentKeySector = null;
            _currentKeyIndexInLeaf = -1;
            _currentKeyIndex = -1;
            _prefix = EmptyByteArray;
            _prefixKeyStart = 0;
            _prefixKeyCount = (long)_readLink.KeyValuePairCount;
        }

        public void Dispose()
        {
            InvalidateCurrentKey();
            if (_readLink != null)
            {
                _owner.DisposeReadTransaction(_readLink);
            }
            else
            {
                _owner.DisposeWriteTransaction();
            }
        }

        private void UpgradeToWriteTransaction()
        {
            if (IsWriteTransaction()) return;
            _owner.UpgradeTransactionToWriteOne(this, _readLink);
            _readLink = null;
            var sector = _currentKeySector;
            for (int i = _currentKeySectorParents.Count - 1; i >= 0; i--)
            {
                var sectorParent = _currentKeySectorParents[i];
                sector.Parent = sectorParent;
                sector = sectorParent;
            }
        }

        public void SetKeyPrefix(byte[] prefix, int prefixOfs, int prefixLen)
        {
            _prefix = EmptyByteArray;
            if (prefixLen == 0)
            {
                _prefixKeyStart = 0;
                _prefixKeyCount = (long)(IsWriteTransaction() ? _owner.NewState.KeyValuePairCount : _readLink.KeyValuePairCount);
                InvalidateCurrentKey();
                return;
            }
            _prefixKeyStart = 0;
            FindKey(prefix, prefixOfs, prefixLen, FindKeyStrategy.OnlyNext);
            _prefixKeyStart = _currentKeyIndex;
            _prefixKeyCount = 0;
            if (GetKeySize() >= prefixLen)
            {
                var buf = new byte[prefixLen];
                ReadKey(0, prefixLen, buf, 0);
                if (BitArrayManipulation.CompareByteArray(prefix, prefixOfs, prefixLen, buf, 0, prefixLen) == 0)
                {
                    _prefixKeyCount = -1;
                }
            }
            _prefix = new byte[prefixLen];
            Array.Copy(prefix, prefixOfs, _prefix, 0, prefixLen);
            InvalidateCurrentKey();
        }

        public void InvalidateCurrentKey()
        {
            _currentKeyIndexInLeaf = -1;
            UnlockCurrentKeySector();
            _currentKeyIndex = -1;
        }

        void UnlockCurrentKeySector()
        {
            if (_currentKeySector != null)
            {
                _currentKeySector.Unlock();
                _currentKeySector = null;
            }
            _currentKeySectorParents.ForEach(s => s.Unlock());
            _currentKeySectorParents.Clear();
        }

        public bool FindFirstKey()
        {
            if (_prefixKeyCount == 0)
            {
                InvalidateCurrentKey();
                return false;
            }
            FindKey(EmptyByteArray, 0, 0, FindKeyStrategy.OnlyNext);
            return true;
        }

        public bool FindLastKey()
        {
            if (_prefixKeyCount == 0)
            {
                InvalidateCurrentKey();
                return false;
            }
            FindKey(EmptyByteArray, -1, 0, FindKeyStrategy.OnlyPrevious);
            _prefixKeyCount = _currentKeyIndex - _prefixKeyStart + 1;
            return true;
        }

        public bool FindPreviousKey()
        {
            if (_currentKeyIndexInLeaf < 0) throw new BTDBException("Current Key is invalid");
            if (_currentKeyIndex == _prefixKeyStart) return false;
            try
            {
                if (_currentKeyIndexInLeaf > 0)
                {
                    _currentKeyIndexInLeaf--;
                    _currentKeyIndex--;
                    _owner.UpdateLastAccess(_currentKeySector);
                    return true;
                }
                var sector = _currentKeySector;
                var parent = PopCurrentKeyParent();
                while (parent != null)
                {
                    var iter = new BTreeParentIterator(parent.Data);
                    var childByPos = iter.FindChildByPos(sector.Position);
                    if (childByPos == 0)
                    {
                        sector = parent;
                        parent = PopCurrentKeyParent();
                        continue;
                    }
                    var childSectorPtr = iter.GetChildSectorPtr(childByPos - 1);
                    while (true)
                    {
                        sector = LoadBTreeSector(childSectorPtr);
                        if (sector.Type == SectorType.BTreeChild)
                        {
                            _currentKeyIndexInLeaf = sector.Data[0] - 1;
                            _currentKeyIndex--;
                            return true;
                        }
                        iter = new BTreeParentIterator(sector.Data);
                        childSectorPtr = iter.GetChildSectorPtr(iter.Count);
                    }
                }
                throw new BTDBException("Internal error");
            }
            catch
            {
                InvalidateCurrentKey();
                throw;
            }
        }

        Sector PopCurrentKeyParent()
        {
            var count = _currentKeySectorParents.Count;
            if (count == 0) return null;
            _currentKeySector.Unlock();
            var parent = _currentKeySectorParents[count - 1];
            _currentKeySectorParents.RemoveAt(count - 1);
            _currentKeySector = parent;
            return parent;
        }

        public bool FindNextKey()
        {
            if (_currentKeyIndexInLeaf < 0) throw new BTDBException("Current Key is invalid");
            if (_prefixKeyCount != -1 && _currentKeyIndex + 1 >= _prefixKeyStart + _prefixKeyCount) return false;
            try
            {
                if (_currentKeyIndexInLeaf + 1 < _currentKeySector.Data[0])
                {
                    _owner.UpdateLastAccess(_currentKeySector);
                    _currentKeyIndexInLeaf++;
                    if (CheckPrefix())
                    {
                        _currentKeyIndex++;
                        return true;
                    }
                    _prefixKeyCount = _currentKeyIndex - _prefixKeyStart + 1;
                    _currentKeyIndexInLeaf--;
                    return false;
                }
                var sector = _currentKeySector;
                var parent = PopCurrentKeyParent();
                while (parent != null)
                {
                    var iter = new BTreeParentIterator(parent.Data);
                    var childByPos = iter.FindChildByPos(sector.Position);
                    if (childByPos == iter.Count)
                    {
                        sector = parent;
                        parent = PopCurrentKeyParent();
                        if (parent == null)
                        {
                            _prefixKeyCount = _currentKeyIndex - _prefixKeyStart + 1;
                            FindLastKey();
                            return false;
                        }
                        continue;
                    }
                    var childSectorPtr = iter.GetChildSectorPtr(childByPos + 1);
                    while (true)
                    {
                        sector = LoadBTreeSector(childSectorPtr);
                        if (sector.Type == SectorType.BTreeChild)
                        {
                            _currentKeyIndexInLeaf = 0;
                            _currentKeyIndex++;
                            if (CheckPrefix())
                            {
                                return true;
                            }
                            _prefixKeyCount = _currentKeyIndex - _prefixKeyStart;
                            FindPreviousKey();
                            return false;
                        }
                        iter = new BTreeParentIterator(sector.Data);
                        childSectorPtr = iter.GetChildSectorPtr(0);
                    }
                }
                throw new BTDBException("Internal error");
            }
            catch
            {
                InvalidateCurrentKey();
                throw;
            }
        }

        bool CheckPrefix()
        {
            if (_prefix.Length == 0) return true;
            var backupPrefix = _prefix;
            _prefix = EmptyByteArray;
            try
            {
                if (GetKeySize() < backupPrefix.Length)
                    return false;
                var prefixLen = backupPrefix.Length;
                var buf = new byte[prefixLen];
                ReadKey(0, prefixLen, buf, 0);
                if (BitArrayManipulation.CompareByteArray(backupPrefix, 0, prefixLen, buf, 0, prefixLen) == 0)
                    return true;
                return false;
            }
            finally
            {
                _prefix = backupPrefix;
            }
        }

        public FindKeyResult FindKey(byte[] keyBuf, int keyOfs, int keyLen, FindKeyStrategy strategy)
        {
            if (keyLen < 0) throw new ArgumentOutOfRangeException("keyLen");
            if (strategy == FindKeyStrategy.Create) UpgradeToWriteTransaction();
            InvalidateCurrentKey();
            var rootBTree = GetRootBTreeSectorPtr();
            if (rootBTree.Ptr == 0)
            {
                return FindKeyInEmptyBTree(keyBuf, keyOfs, keyLen, strategy);
            }
            Sector sector;
            try
            {
                long keyIndex = 0;
                while (true)
                {
                    sector = LoadBTreeSector(rootBTree);
                    if (sector.Type == SectorType.BTreeChild) break;
                    var iterParent = new BTreeParentIterator(sector.Data);
                    int bindexParent = iterParent.BinarySearch(_prefix, keyBuf, keyOfs, keyLen, sector, SectorDataCompare);
                    rootBTree = iterParent.GetChildSectorPtr((bindexParent + 1) / 2, ref keyIndex);
                }
                var iter = new BTreeChildIterator(sector.Data);
                int bindex = iter.BinarySearch(_prefix, keyBuf, keyOfs, keyLen, sector, SectorDataCompare);
                _currentKeyIndexInLeaf = bindex / 2;
                _currentKeyIndex = keyIndex + _currentKeyIndexInLeaf;
                if ((bindex & 1) != 0)
                {
                    return FindKeyResult.FoundExact;
                }
                if (strategy != FindKeyStrategy.Create)
                {
                    return FindKeyNoncreateStrategy(strategy, iter);
                }
                int additionalLengthNeeded = BTreeChildIterator.HeaderForEntry + BTreeChildIterator.CalcEntrySize(_prefix.Length + keyLen);
                if (_owner.Tweaks.ShouldSplitBTreeChild(iter.TotalLength, additionalLengthNeeded, iter.Count))
                {
                    SplitBTreeChild(keyBuf, keyOfs, keyLen, sector, iter, additionalLengthNeeded);
                }
                else
                {
                    sector = _owner.ResizeSectorWithUpdatePosition(sector, iter.TotalLength + additionalLengthNeeded,
                                                                   sector.Parent, _currentKeySectorParents);
                    _currentKeySector = sector;
                    int insertOfs = iter.AddEntry(additionalLengthNeeded, sector.Data, _currentKeyIndexInLeaf);
                    SetBTreeChildKeyData(sector, keyBuf, keyOfs, keyLen, insertOfs);
                    IncrementChildCountInBTreeParents(sector);
                }
                _owner.NewState.KeyValuePairCount++;
                if (_prefixKeyCount != -1) _prefixKeyCount++;
                _owner.TruncateSectorCache(true);
                return FindKeyResult.Created;
            }
            catch
            {
                InvalidateCurrentKey();
                throw;
            }
        }

        void SplitBTreeChild(byte[] keyBuf, int keyOfs, int keyLen, Sector sector, BTreeChildIterator iter, int additionalLengthNeeded)
        {
            Sector rightSector = null;
            bool unlockRightSector = false;
            try
            {
                int middleoffset = (iter.TotalLength + iter.FirstOffset + additionalLengthNeeded) / 2;
                iter.MoveFirst();
                bool beforeNew = true;
                int splitIndex = 0;
                int currentPos = iter.FirstOffset;
                while (currentPos < middleoffset)
                {
                    if (beforeNew && splitIndex == _currentKeyIndexInLeaf)
                    {
                        beforeNew = false;
                        currentPos += additionalLengthNeeded - BTreeChildIterator.HeaderForEntry;
                    }
                    else
                    {
                        currentPos += iter.CurrentEntrySize;
                        splitIndex++;
                        iter.MoveNext();
                    }
                }
                rightSector = _owner.NewSector();
                rightSector.Type = SectorType.BTreeChild;
                int rightCount = iter.Count - splitIndex + (beforeNew ? 1 : 0);
                rightSector.SetLengthWithRound(BTreeChildIterator.HeaderSize + rightCount * BTreeChildIterator.HeaderForEntry + iter.TotalLength + additionalLengthNeeded - BTreeChildIterator.HeaderForEntry - currentPos);
                BTreeChildIterator.SetCountToSectorData(rightSector.Data, rightCount);
                rightSector.Parent = sector.Parent;
                _owner.PublishSector(rightSector);
                unlockRightSector = true;
                int leftCount = splitIndex + (beforeNew ? 0 : 1);
                Sector leftSector = _owner.ResizeSectorWithUpdatePosition(sector, BTreeChildIterator.HeaderSize + leftCount * BTreeChildIterator.HeaderForEntry + currentPos - iter.FirstOffset, sector.Parent,
                                                                          _currentKeySectorParents);
                _currentKeySector = leftSector;
                Sector newKeySector;
                BTreeChildIterator.SetCountToSectorData(leftSector.Data, leftCount);
                int newItemPos = iter.OffsetOfIndex(_currentKeyIndexInLeaf);
                if (beforeNew)
                {
                    Array.Copy(iter.Data, iter.FirstOffset, leftSector.Data, BTreeChildIterator.HeaderSize + BTreeChildIterator.HeaderForEntry * leftCount, currentPos - iter.FirstOffset);
                    Array.Copy(iter.Data, currentPos, rightSector.Data, BTreeChildIterator.HeaderSize + BTreeChildIterator.HeaderForEntry * rightCount, newItemPos - currentPos);
                    int rightPos = BTreeChildIterator.HeaderSize + BTreeChildIterator.HeaderForEntry * rightCount + newItemPos - currentPos;
                    SetBTreeChildKeyData(rightSector, keyBuf, keyOfs, keyLen, rightPos);
                    rightPos += additionalLengthNeeded - BTreeChildIterator.HeaderForEntry;
                    Array.Copy(iter.Data, newItemPos, rightSector.Data, rightPos, iter.TotalLength - newItemPos);
                    newKeySector = rightSector;
                    _currentKeyIndexInLeaf -= splitIndex;
                }
                else
                {
                    Array.Copy(iter.Data, iter.FirstOffset, leftSector.Data, BTreeChildIterator.HeaderSize + BTreeChildIterator.HeaderForEntry * leftCount, newItemPos - iter.FirstOffset);
                    int leftPosInsert = BTreeChildIterator.HeaderSize + BTreeChildIterator.HeaderForEntry * leftCount + newItemPos - iter.FirstOffset;
                    int leftPos = leftPosInsert;
                    leftPos += additionalLengthNeeded - BTreeChildIterator.HeaderForEntry;
                    Array.Copy(iter.Data, currentPos - additionalLengthNeeded + BTreeChildIterator.HeaderForEntry, rightSector.Data, BTreeChildIterator.HeaderSize + BTreeChildIterator.HeaderForEntry * rightCount,
                               iter.TotalLength + additionalLengthNeeded - BTreeChildIterator.HeaderForEntry - currentPos);
                    Array.Copy(iter.Data, newItemPos, leftSector.Data, leftPos, currentPos - newItemPos - additionalLengthNeeded + BTreeChildIterator.HeaderForEntry);
                    SetBTreeChildKeyData(leftSector, keyBuf, keyOfs, keyLen, leftPosInsert);
                    newKeySector = leftSector;
                }
                BTreeChildIterator.RecalculateHeader(leftSector.Data, leftCount);
                BTreeChildIterator.RecalculateHeader(rightSector.Data, rightCount);
                FixChildrenParentPointers(leftSector);
                FixChildrenParentPointers(rightSector);
                if (leftSector.Parent == null)
                {
                    CreateBTreeParentFromTwoLeafs(leftSector, rightSector);
                }
                else
                {
                    iter = new BTreeChildIterator(rightSector.Data);
                    if (iter.HasKeySectorPtr) ForceKeyFlush(iter.KeySectorPtr, iter.KeyLen - iter.KeyLenInline, rightSector);
                    int keyLenInSector = iter.KeyLenInline + (iter.HasKeySectorPtr ? LowLevelDB.PtrDownSize : 0);
                    AddToBTreeParent(leftSector, rightSector, iter.Data, iter.KeyLen, iter.KeyOffset,
                                     keyLenInSector);
                }
                UnlockUselessAndFixKeySectorParents(newKeySector, leftSector, rightSector);
                unlockRightSector = false;
                _currentKeySector = newKeySector;
            }
            finally
            {
                if (unlockRightSector) rightSector.Unlock();
            }
        }

        void ForceKeyFlush(SectorPtr keySectorPtr, int keySize, Sector parent)
        {
            // Because parent of sector could be just one and we are going to point to same sector from 2 places it needs to be forcibly flushed
            ForceFlushContentSector(keySectorPtr, keySize, parent);
        }

        void ForceFlushContentSector(SectorPtr sectorPtr, long len, Sector parent)
        {
            Sector sector = null;
            try
            {
                sector = _owner.TryGetSector(sectorPtr.Ptr);
                if (len <= LowLevelDB.MaxLeafDataSectorSize)
                {
                    if (sector == null) return;
                    _owner.ForceFlushSector(sector);
                    return;
                }
                int downPtrCount;
                long bytesInDownLevel = GetBytesInDownLevel(len, out downPtrCount);
                if (sector == null)
                {
                    sector = _owner.ReadSector(sectorPtr, true);
                    sector.Type = SectorType.DataParent;
                    sector.Parent = parent;
                }
                for (int i = 0; i < downPtrCount; i++)
                {
                    var downSectorPtr = SectorPtr.Unpack(sector.Data, i * LowLevelDB.PtrDownSize);
                    ForceFlushContentSector(downSectorPtr, Math.Min(len, bytesInDownLevel), sector);
                    len -= bytesInDownLevel;
                }
                _owner.ForceFlushSector(sector);
            }
            finally
            {
                if (sector != null) sector.Unlock();
            }
        }

        void UnlockUselessAndFixKeySectorParents(Sector newKeySector, Sector leftSector, Sector rightSector)
        {
            if (newKeySector == leftSector)
            {
                do
                {
                    rightSector.Unlock();
                    leftSector = leftSector.Parent;
                    rightSector = rightSector.Parent;
                } while (leftSector != rightSector);
            }
            else
            {
                var pi = _currentKeySectorParents.Count;
                do
                {
                    if (pi < _currentKeySectorParents.Count)
                    {
                        Debug.Assert(_currentKeySectorParents[pi] == leftSector);
                        _currentKeySectorParents[pi] = rightSector;
                    }
                    leftSector.Unlock();
                    leftSector = leftSector.Parent;
                    rightSector = rightSector.Parent;
                    pi--;
                } while (leftSector != rightSector);
            }
        }

        public long GetKeyValueCount()
        {
            if (_prefixKeyCount != -1)
            {
                InvalidateCurrentKey();
                return _prefixKeyCount;
            }
            FindLastKey();
            InvalidateCurrentKey();
            return _prefixKeyCount;
        }

        public long GetKeyIndex()
        {
            return _currentKeyIndex - _prefixKeyStart;
        }

        Sector LoadBTreeSector(SectorPtr sectorPtr)
        {
            Sector sector = _owner.TryGetSector(sectorPtr.Ptr);
            if (sector == null)
            {
                sector = _owner.ReadSector(sectorPtr, IsWriteTransaction());
                try
                {
                    sector.Type = (sector.Data[0] & 0x80) != 0 ? SectorType.BTreeParent : SectorType.BTreeChild;
                    sector.Parent = _currentKeySector;
                    if (_currentKeySector != null) _currentKeySectorParents.Add(_currentKeySector);
                    _currentKeySector = sector;
                }
                catch
                {
                    sector.Unlock();
                    throw;
                }
            }
            else
            {
                if (IsWriteTransaction()) sector.Parent = _currentKeySector;
                try
                {
                    if (_currentKeySector != null) _currentKeySectorParents.Add(_currentKeySector);
                    _currentKeySector = sector;
                }
                catch
                {
                    sector.Unlock();
                    throw;
                }
            }
            return sector;
        }

        void AddToBTreeParent(Sector leftSector, Sector rightSector, byte[] middleKeyData, int middleKeyLen, int middleKeyOfs, int middleKeyLenInSector)
        {
            var parentSector = leftSector.Parent;
            var iter = new BTreeParentIterator(parentSector.Data);
            int additionalLengthNeeded = BTreeParentIterator.HeaderForEntry + BTreeParentIterator.CalcEntrySize(middleKeyLen);
            int leftIndexInParent = iter.FindChildByPos(leftSector.Position);
            bool splitting = true;
            if (!_owner.Tweaks.ShouldSplitBTreeParent(iter.TotalLength, additionalLengthNeeded, iter.Count + 1))
            {
                parentSector = _owner.ResizeSectorWithUpdatePosition(parentSector,
                                                                     iter.TotalLength + additionalLengthNeeded,
                                                                     parentSector.Parent,
                                                                     _currentKeySectorParents);
                splitting = false;
            }
            var mergedData = new byte[iter.TotalLength + additionalLengthNeeded];
            BTreeParentIterator.SetCountToSectorData(mergedData, iter.Count + 1);
            int ofs;
            int splitOfs = iter.OffsetOfIndex(leftIndexInParent);
            if (leftIndexInParent == 0)
            {
                SectorPtr.Pack(mergedData, BTreeParentIterator.FirstChildSectorPtrOffset, leftSector.ToSectorPtr());
                PackUnpack.PackUInt64(mergedData, BTreeParentIterator.FirstChildSectorPtrOffset + LowLevelDB.PtrDownSize, CalcKeysInSector(leftSector));
                ofs = BTreeParentIterator.HeaderSize + BTreeParentIterator.HeaderForEntry * (iter.Count + 1);
            }
            else
            {
                Array.Copy(iter.Data, BTreeParentIterator.FirstChildSectorPtrOffset, mergedData, BTreeParentIterator.FirstChildSectorPtrOffset, LowLevelDB.PtrDownSize + 8);
                ofs = BTreeParentIterator.HeaderSize + BTreeParentIterator.HeaderForEntry * (iter.Count + 1);
                int splitOfsPrev = iter.OffsetOfIndex(leftIndexInParent - 1);
                Array.Copy(iter.Data, iter.FirstOffset, mergedData, ofs, splitOfsPrev - iter.FirstOffset);
                ofs += splitOfsPrev - iter.FirstOffset;
                Array.Copy(iter.Data, splitOfsPrev, mergedData, ofs, 4 + LowLevelDB.PtrDownSize);
                ofs += 4 + LowLevelDB.PtrDownSize;
                PackUnpack.PackUInt64(mergedData, ofs, CalcKeysInSector(leftSector));
                ofs += 8;
                splitOfsPrev += 4 + LowLevelDB.PtrDownSize + 8;
                Array.Copy(iter.Data, splitOfsPrev, mergedData, ofs, splitOfs - splitOfsPrev);
                ofs += splitOfs - splitOfsPrev;
            }
            PackUnpack.PackInt32(mergedData, ofs, middleKeyLen);
            ofs += 4;
            SectorPtr.Pack(mergedData, ofs, rightSector.ToSectorPtr());
            ofs += LowLevelDB.PtrDownSize;
            PackUnpack.PackUInt64(mergedData, ofs, CalcKeysInSector(rightSector));
            ofs += 8;
            Array.Copy(middleKeyData, middleKeyOfs, mergedData, ofs, middleKeyLenInSector);
            ofs += middleKeyLenInSector;
            Array.Copy(iter.Data, splitOfs, mergedData, ofs, iter.TotalLength - splitOfs);
            BTreeParentIterator.RecalculateHeader(mergedData, iter.Count + 1);
            if (!splitting)
            {
                Array.Copy(mergedData, parentSector.Data, mergedData.Length);
                leftSector.Parent = parentSector;
                rightSector.Parent = parentSector;
                IncrementChildCountInBTreeParents(parentSector);
            }
            else
            {
                iter = new BTreeParentIterator(mergedData);
                int middleoffset = mergedData.Length / 2;
                iter.MoveFirst();
                int splitIndex = 0;
                int currentPos = iter.FirstOffset;
                while (currentPos < middleoffset)
                {
                    currentPos += iter.CurrentEntrySize;
                    splitIndex++;
                    iter.MoveNext();
                }
                Sector leftParentSector = null;
                Sector rightParentSector = null;
                try
                {
                    rightParentSector = _owner.NewSector();
                    rightParentSector.Type = SectorType.BTreeParent;
                    var rightCount = iter.Count - splitIndex - 1;
                    var rightFirstOffset = BTreeParentIterator.HeaderSize + rightCount * BTreeParentIterator.HeaderForEntry;
                    rightParentSector.SetLengthWithRound(rightFirstOffset + mergedData.Length - iter.NextEntryOffset);
                    BTreeParentIterator.SetCountToSectorData(rightParentSector.Data, rightCount);
                    rightParentSector.Parent = parentSector.Parent;
                    var leftFirstOffset = BTreeParentIterator.HeaderSize + splitIndex * BTreeParentIterator.HeaderForEntry;
                    leftParentSector = _owner.ResizeSectorWithUpdatePosition(parentSector,
                                                                             leftFirstOffset + currentPos -
                                                                             iter.FirstOffset, parentSector.Parent,
                                                                             _currentKeySectorParents);
                    BTreeParentIterator.SetCountToSectorData(leftParentSector.Data, splitIndex);
                    leftSector.Parent = leftParentSector;
                    rightSector.Parent = leftParentSector;
                    Array.Copy(mergedData, BTreeParentIterator.FirstChildSectorPtrOffset, leftParentSector.Data,
                               BTreeParentIterator.FirstChildSectorPtrOffset,
                               LowLevelDB.PtrDownSize + 8 + BTreeParentIterator.HeaderForEntry * splitIndex);
                    Array.Copy(mergedData, iter.FirstOffset, leftParentSector.Data, leftFirstOffset,
                               currentPos - iter.FirstOffset);
                    Array.Copy(mergedData, iter.ChildSectorPtrOffset, rightParentSector.Data, BTreeParentIterator.FirstChildSectorPtrOffset, LowLevelDB.PtrDownSize + 8);
                    Array.Copy(mergedData, iter.NextEntryOffset, rightParentSector.Data, rightFirstOffset, iter.TotalLength - iter.NextEntryOffset);
                    BTreeParentIterator.RecalculateHeader(rightParentSector.Data, rightCount);
                    FixChildrenParentPointers(rightParentSector);
                    _owner.PublishSector(rightParentSector);
                    int keyLenInSector = iter.KeyLenInline + (iter.HasKeySectorPtr ? LowLevelDB.PtrDownSize : 0);
                    if (leftParentSector.Parent == null)
                    {
                        CreateBTreeParentFromTwoChildren(leftParentSector, rightParentSector, iter.Data, iter.KeyLen, iter.KeyOffset, keyLenInSector);
                    }
                    else
                    {
                        AddToBTreeParent(leftParentSector, rightParentSector, iter.Data, iter.KeyLen, iter.KeyOffset, keyLenInSector);
                    }
                }
                finally
                {
                    if (leftSector.Parent == rightParentSector)
                    {
                        leftParentSector.Unlock();
                        for (int i = 0; i < _currentKeySectorParents.Count; i++)
                        {
                            if (_currentKeySectorParents[i] == leftParentSector)
                            {
                                _currentKeySectorParents[i] = rightParentSector;
                                break;
                            }
                        }
                    }
                    if (rightSector.Parent == leftParentSector)
                    {
                        rightParentSector.Unlock();
                    }
                }
            }
        }

        void FixChildrenParentPointers(Sector parent)
        {
            switch (parent.Type)
            {
                case SectorType.BTreeParent:
                    {
                        var iter = new BTreeParentIterator(parent.Data);
                        for (int i = 0; i <= iter.Count; i++)
                        {
                            var childSectorPos = iter.GetChildSectorPos(i);
                            _owner.FixChildParentPointer(childSectorPos, parent);
                        }
                        break;
                    }
                case SectorType.BTreeChild:
                    {
                        var iter = new BTreeChildIterator(parent.Data);
                        do
                        {
                            if (iter.HasKeySectorPtr)
                                _owner.FixChildParentPointer(iter.KeySectorPos, parent);
                            if (iter.HasValueSectorPtr)
                                _owner.FixChildParentPointer(iter.ValueSectorPos, parent);
                        } while (iter.MoveNext());
                        break;
                    }
                default:
                    throw new InvalidOperationException();
            }
        }

        void CreateBTreeParentFromTwoChildren(Sector leftSector, Sector rightSector, byte[] middleKeyData, int middleKeyLen, int middleKeyOfs, int middleKeyLenInSector)
        {
            Sector parentSector = _owner.NewSector();
            parentSector.Type = SectorType.BTreeParent;
            var entrySize = BTreeParentIterator.CalcEntrySize(middleKeyLen);
            parentSector.SetLengthWithRound(BTreeParentIterator.HeaderSize + BTreeParentIterator.HeaderForEntry + entrySize);
            BTreeParentIterator.SetCountToSectorData(parentSector.Data, 1);
            int ofs = 2;
            SectorPtr.Pack(parentSector.Data, ofs, leftSector.ToSectorPtr());
            ofs += LowLevelDB.PtrDownSize;
            PackUnpack.PackUInt64(parentSector.Data, ofs, CalcKeysInSector(leftSector));
            ofs += 8;
            PackUnpack.PackUInt16(parentSector.Data, ofs, (ushort)entrySize);
            ofs += 2;
            PackUnpack.PackUInt32(parentSector.Data, ofs, (uint)middleKeyLen);
            ofs += 4;
            SectorPtr.Pack(parentSector.Data, ofs, rightSector.ToSectorPtr());
            ofs += LowLevelDB.PtrDownSize;
            PackUnpack.PackUInt64(parentSector.Data, ofs, CalcKeysInSector(rightSector));
            ofs += 8;
            Array.Copy(middleKeyData, middleKeyOfs, parentSector.Data, ofs, middleKeyLenInSector);
            _owner.NewState.RootBTree.Ptr = parentSector.Position;
            _owner.NewState.RootBTreeLevels++;
            leftSector.Parent = parentSector;
            rightSector.Parent = parentSector;
            _currentKeySectorParents.Insert(0, parentSector);
            _owner.PublishSector(parentSector);
        }

        static ulong CalcKeysInSector(Sector sector)
        {
            if (sector.Type == SectorType.BTreeChild)
            {
                return BTreeChildIterator.CountFromSectorData(sector.Data);
            }
            Debug.Assert(sector.Type == SectorType.BTreeParent);
            var iter = new BTreeParentIterator(sector.Data);
            var res = (ulong)iter.FirstChildKeyCount;
            do
            {
                res += (ulong)iter.ChildKeyCount;
            } while (iter.MoveNext());
            return res;
        }

        static void IncrementChildCountInBTreeParents(Sector sector)
        {
            while (sector.Parent != null)
            {
                Sector parentSector = sector.Parent;
                Debug.Assert(parentSector.Dirty);
                BTreeParentIterator.IncrementChildCount(parentSector.Data, sector.Position);
                sector = parentSector;
            }
        }

        FindKeyResult FindKeyInEmptyBTree(byte[] keyBuf, int keyOfs, int keyLen, FindKeyStrategy strategy)
        {
            switch (strategy)
            {
                case FindKeyStrategy.Create:
                    var newRootBTreeSector1 = _owner.NewSector();
                    newRootBTreeSector1.Type = SectorType.BTreeChild;
                    var entrySize = BTreeChildIterator.CalcEntrySize(_prefix.Length + keyLen);
                    newRootBTreeSector1.SetLengthWithRound(BTreeChildIterator.HeaderSize + BTreeChildIterator.HeaderForEntry + entrySize);
                    BTreeChildIterator.SetOneEntryCount(newRootBTreeSector1.Data, entrySize);
                    SetBTreeChildKeyData(newRootBTreeSector1, keyBuf, keyOfs, keyLen, BTreeChildIterator.HeaderSize + BTreeChildIterator.HeaderForEntry);
                    Sector newRootBTreeSector = newRootBTreeSector1;
                    _owner.NewState.RootBTree.Ptr = newRootBTreeSector.Position;
                    _owner.NewState.RootBTreeLevels = 1;
                    _owner.NewState.KeyValuePairCount = 1;
                    _owner.PublishSector(newRootBTreeSector);
                    _currentKeySector = newRootBTreeSector;
                    _currentKeyIndexInLeaf = 0;
                    _currentKeyIndex = 0;
                    _prefixKeyStart = 0;
                    _prefixKeyCount = 1;
                    return FindKeyResult.Created;
                case FindKeyStrategy.ExactMatch:
                case FindKeyStrategy.PreferPrevious:
                case FindKeyStrategy.PreferNext:
                case FindKeyStrategy.OnlyPrevious:
                case FindKeyStrategy.OnlyNext:
                    return FindKeyNotFound();
                default:
                    throw new ArgumentOutOfRangeException("strategy");
            }
        }

        SectorPtr GetRootBTreeSectorPtr()
        {
            return IsWriteTransaction() ? _owner.NewState.RootBTree : _readLink.RootBTree;
        }

        void CreateBTreeParentFromTwoLeafs(Sector leftSector, Sector rightSector)
        {
            var iter = new BTreeChildIterator(rightSector.Data);
            int keyLenInSector = iter.KeyLenInline + (iter.HasKeySectorPtr ? LowLevelDB.PtrDownSize : 0);
            CreateBTreeParentFromTwoChildren(leftSector, rightSector, iter.Data, iter.KeyLen, iter.KeyOffset,
                                            keyLenInSector);
        }

        FindKeyResult FindKeyNoncreateStrategy(FindKeyStrategy strategy, BTreeChildIterator iter)
        {
            switch (strategy)
            {
                case FindKeyStrategy.ExactMatch:
                    return FindKeyNotFound();
                case FindKeyStrategy.OnlyNext:
                    if (_currentKeyIndexInLeaf < iter.Count)
                    {
                        return FindKeyResult.FoundNext;
                    }
                    _currentKeyIndexInLeaf--;
                    return FindNextKey() ? FindKeyResult.FoundNext : FindKeyNotFound();
                case FindKeyStrategy.PreferNext:
                    if (_currentKeyIndexInLeaf < iter.Count)
                    {
                        return FindKeyResult.FoundNext;
                    }
                    _currentKeyIndexInLeaf--;
                    return FindNextKey() ? FindKeyResult.FoundNext : FindKeyResult.FoundPrevious;
                case FindKeyStrategy.OnlyPrevious:
                    if (_currentKeyIndexInLeaf > 0)
                    {
                        _currentKeyIndexInLeaf--;
                        _currentKeyIndex--;
                        return FindKeyResult.FoundPrevious;
                    }
                    return FindPreviousKey() ? FindKeyResult.FoundPrevious : FindKeyNotFound();
                case FindKeyStrategy.PreferPrevious:
                    if (_currentKeyIndexInLeaf > 0)
                    {
                        _currentKeyIndexInLeaf--;
                        _currentKeyIndex--;
                        return FindKeyResult.FoundPrevious;
                    }
                    return FindPreviousKey() ? FindKeyResult.FoundPrevious : FindKeyResult.FoundNext;
                default:
                    throw new ArgumentOutOfRangeException("strategy");
            }
        }

        FindKeyResult FindKeyNotFound()
        {
            InvalidateCurrentKey();
            return FindKeyResult.NotFound;
        }

        int SectorDataCompare(int startOfs, byte[] buf, int ofs, int len, SectorPtr sectorPtr, int dataLen, Sector parent)
        {
            Sector sector = null;
            try
            {
                sector = _owner.TryGetSector(sectorPtr.Ptr);
                if (sector == null)
                {
                    sector = _owner.ReadSector(sectorPtr, IsWriteTransaction());
                    sector.Type = dataLen > sector.Length ? SectorType.DataParent : SectorType.DataChild;
                    sector.Parent = parent;
                }
                if (sector.Type == SectorType.DataChild)
                {
                    int dataOfs = 0;
                    if (startOfs < _prefix.Length)
                    {
                        int compareLen = Math.Min(_prefix.Length - startOfs, sector.Length);
                        int res = BitArrayManipulation.CompareByteArray(_prefix, startOfs, compareLen, sector.Data, dataOfs,
                                                                        compareLen);
                        if (res != 0) return res;
                        startOfs += compareLen;
                        if (startOfs < _prefix.Length) return 0;
                        dataOfs += compareLen;
                    }
                    if (ofs == -1) return 1;
                    startOfs -= _prefix.Length;
                    return BitArrayManipulation.CompareByteArray(buf,
                                                                 ofs + startOfs,
                                                                 Math.Min(len - startOfs, sector.Length - dataOfs),
                                                                 sector.Data,
                                                                 dataOfs,
                                                                 sector.Length - dataOfs);
                }
                int downPtrCount;
                var bytesInDownLevel = (int)GetBytesInDownLevel(dataLen, out downPtrCount);
                int i;
                SectorPtr downSectorPtr;
                for (i = 0; i < downPtrCount - 1; i++)
                {
                    downSectorPtr = SectorPtr.Unpack(sector.Data, i * LowLevelDB.PtrDownSize);
                    int res = SectorDataCompare(startOfs,
                                                buf,
                                                ofs,
                                                len,
                                                downSectorPtr,
                                                Math.Min(dataLen, bytesInDownLevel),
                                                sector);
                    if (res != 0) return res;
                    startOfs += bytesInDownLevel;
                    dataLen -= bytesInDownLevel;
                }
                downSectorPtr = SectorPtr.Unpack(sector.Data, i * LowLevelDB.PtrDownSize);
                return SectorDataCompare(startOfs, buf, ofs, len, downSectorPtr, dataLen, sector);
            }
            finally
            {
                if (sector != null) sector.Unlock();
            }
        }

        void SetBTreeChildKeyData(Sector inSector, byte[] keyBuf, int keyOfs, int keyLen, int sectorDataOfs)
        {
            byte[] sectorData = inSector.Data;
            int realKeyLen = _prefix.Length + keyLen;
            int keyLenInline = BTreeChildIterator.CalcKeyLenInline(realKeyLen);
            PackUnpack.PackUInt32(sectorData, sectorDataOfs, (uint)realKeyLen);
            PackUnpack.PackUInt64(sectorData, sectorDataOfs + 4, 0);
            sectorDataOfs += 4 + 8;
            var usedPrefixLen = Math.Min(_prefix.Length, keyLenInline);
            Array.Copy(_prefix, 0, sectorData, sectorDataOfs, usedPrefixLen);
            Array.Copy(keyBuf, keyOfs, sectorData, sectorDataOfs + _prefix.Length, keyLenInline - usedPrefixLen);
            sectorDataOfs += keyLenInline;
            if (realKeyLen > BTreeChildIterator.MaxKeyLenInline)
            {
                SectorPtr keySecPtr = CreateContentSector(_prefix, keyLenInline, _prefix.Length - usedPrefixLen, keyBuf, keyOfs + keyLenInline - usedPrefixLen, keyLen - keyLenInline + usedPrefixLen, inSector);
                SectorPtr.Pack(sectorData, sectorDataOfs, keySecPtr);
            }
        }

        SectorPtr CreateContentSector(byte[] buf, int ofs, int len, Sector parent)
        {
            return CreateContentSector(buf, ofs, len, null, 0, 0, parent);
        }

        SectorPtr CreateContentSector(byte[] buf, int ofs, int len, byte[] buf2, int ofs2, int len2, Sector parent)
        {
            if (len + len2 <= LowLevelDB.MaxLeafDataSectorSize)
            {
                var newLeafSector = _owner.NewSector();
                newLeafSector.Type = SectorType.DataChild;
                newLeafSector.SetLengthWithRound(len + len2);
                newLeafSector.Parent = parent;
                if (len > 0) Array.Copy(buf, ofs, newLeafSector.Data, 0, len);
                if (len2 > 0) Array.Copy(buf2, ofs2, newLeafSector.Data, len, len2);
                _owner.PublishSector(newLeafSector);
                _owner.TruncateSectorCache(true);
                newLeafSector.Unlock();
                return newLeafSector.ToSectorPtr();
            }
            int downPtrCount;
            var bytesInDownLevel = (int)GetBytesInDownLevel(len + len2, out downPtrCount);
            var newSector = _owner.NewSector();
            newSector.Type = SectorType.DataParent;
            newSector.SetLengthWithRound(downPtrCount * LowLevelDB.PtrDownSize);
            newSector.Parent = parent;
            for (int i = 0; i < downPtrCount; i++)
            {
                var usedLen = Math.Min(len, bytesInDownLevel);
                var usedLen2 = Math.Min(len2, bytesInDownLevel - usedLen);
                SectorPtr sectorPtr = CreateContentSector(buf, ofs, usedLen, buf2, ofs2, usedLen2, newSector);
                SectorPtr.Pack(newSector.Data, i * LowLevelDB.PtrDownSize, sectorPtr);
                ofs += usedLen;
                ofs2 += usedLen2;
                len -= usedLen;
                len2 -= usedLen2;
            }
            _owner.PublishSector(newSector);
            _owner.TruncateSectorCache(true);
            newSector.Unlock();
            return newSector.ToSectorPtr();
        }

        SectorPtr CreateContentSector(long len, Sector parent)
        {
            if (len <= LowLevelDB.MaxLeafDataSectorSize)
            {
                var newLeafSector = _owner.NewSector();
                newLeafSector.Type = SectorType.DataChild;
                newLeafSector.SetLengthWithRound((int)len);
                newLeafSector.Parent = parent;
                _owner.PublishSector(newLeafSector);
                _owner.TruncateSectorCache(true);
                newLeafSector.Unlock();
                return newLeafSector.ToSectorPtr();
            }
            int downPtrCount;
            long bytesInDownLevel = GetBytesInDownLevel(len, out downPtrCount);
            var newSector = _owner.NewSector();
            newSector.Type = SectorType.DataParent;
            newSector.SetLengthWithRound(downPtrCount * LowLevelDB.PtrDownSize);
            newSector.Parent = parent;
            for (int i = 0; i < downPtrCount; i++)
            {
                SectorPtr sectorPtr = CreateContentSector(Math.Min(len, bytesInDownLevel), newSector);
                SectorPtr.Pack(newSector.Data, i * LowLevelDB.PtrDownSize, sectorPtr);
                len -= bytesInDownLevel;
            }
            _owner.PublishSector(newSector);
            _owner.TruncateSectorCache(true);
            newSector.Unlock();
            return newSector.ToSectorPtr();
        }

        void DeleteContentSector(SectorPtr sectorPtr, long len, Sector parent)
        {
            Sector sector = null;
            try
            {
                sector = _owner.TryGetSector(sectorPtr.Ptr);
                if (len <= LowLevelDB.MaxLeafDataSectorSize)
                {
                    if (sector == null)
                    {
                        sector = _owner.ReadSector(sectorPtr, true);
                        sector.Type = SectorType.DataChild;
                        sector.Parent = parent;
                    }
                    _owner.DeallocateSector(sector);
                    return;
                }
                int downPtrCount;
                long bytesInDownLevel = GetBytesInDownLevel(len, out downPtrCount);
                if (sector == null)
                {
                    sector = _owner.ReadSector(sectorPtr, true);
                    sector.Type = SectorType.DataParent;
                    sector.Parent = parent;
                }
                for (int i = 0; i < downPtrCount; i++)
                {
                    var downSectorPtr = SectorPtr.Unpack(sector.Data, i * LowLevelDB.PtrDownSize);
                    DeleteContentSector(downSectorPtr, Math.Min(len, bytesInDownLevel), sector);
                    len -= bytesInDownLevel;
                }
                _owner.DeallocateSector(sector);
            }
            finally
            {
                if (sector != null) sector.Unlock();
            }
        }

        public int GetKeySize()
        {
            if (_currentKeyIndexInLeaf < 0) return -1;
            var iter = new BTreeChildIterator(_currentKeySector.Data);
            iter.MoveTo(_currentKeyIndexInLeaf);
            return iter.KeyLen - _prefix.Length;
        }

        public long GetValueSize()
        {
            if (_currentKeyIndexInLeaf < 0) return -1;
            var iter = new BTreeChildIterator(_currentKeySector.Data);
            iter.MoveTo(_currentKeyIndexInLeaf);
            return iter.ValueLen;
        }

        public void PeekKey(int ofs, out int len, out byte[] buf, out int bufOfs)
        {
            if (_currentKeyIndexInLeaf < 0) throw new BTDBException("Current Key is invalid");
            ofs += _prefix.Length;
            var iter = new BTreeChildIterator(_currentKeySector.Data);
            iter.MoveTo(_currentKeyIndexInLeaf);
            if (ofs >= iter.KeyLen)
            {
                len = 0;
                buf = null;
                bufOfs = 0;
                return;
            }
            if (ofs < iter.KeyLenInline)
            {
                len = iter.KeyLenInline - ofs;
                buf = _currentKeySector.Data;
                bufOfs = iter.KeyOffset + ofs;
                return;
            }
            ofs -= iter.KeyLenInline;
            SectorPtr dataSectorPtr = iter.KeySectorPtr;
            int dataLen = iter.KeyLen - iter.KeyLenInline;
            Sector parentOfSector = _currentKeySector;
            var unlockStack = new List<Sector>();
            try
            {
                while (true)
                {
                    Sector dataSector = _owner.TryGetSector(dataSectorPtr.Ptr);
                    if (dataLen <= LowLevelDB.MaxLeafDataSectorSize)
                    {
                        if (dataSector == null)
                        {
                            dataSector = _owner.ReadSector(dataSectorPtr, IsWriteTransaction());
                            dataSector.Type = SectorType.DataChild;
                            dataSector.Parent = parentOfSector;
                        }
                        unlockStack.Add(dataSector);
                        buf = dataSector.Data;
                        bufOfs = ofs;
                        len = dataSector.Length - ofs;
                        return;
                    }
                    if (dataSector == null)
                    {
                        dataSector = _owner.ReadSector(dataSectorPtr, IsWriteTransaction());
                        dataSector.Type = SectorType.DataParent;
                        dataSector.Parent = parentOfSector;
                    }
                    unlockStack.Add(dataSector);
                    parentOfSector = dataSector;
                    int downPtrCount;
                    var bytesInDownLevel = (int)GetBytesInDownLevel(dataLen, out downPtrCount);
                    int i = ofs / bytesInDownLevel;
                    ofs = ofs % bytesInDownLevel;
                    dataSectorPtr = SectorPtr.Unpack(dataSector.Data, i * LowLevelDB.PtrDownSize);
                    if (i < downPtrCount - 1)
                    {
                        dataLen = bytesInDownLevel;
                    }
                    else
                    {
                        dataLen = dataLen % bytesInDownLevel;
                        if (dataLen == 0) dataLen = bytesInDownLevel;
                    }
                }
            }
            finally
            {
                unlockStack.ForEach(s => s.Unlock());
            }
        }

        bool IsWriteTransaction()
        {
            return _readLink == null;
        }

        public void ReadKey(int ofs, int len, byte[] buf, int bufOfs)
        {
            while (len > 0)
            {
                byte[] localBuf;
                int localBufOfs;
                int localOutLen;
                PeekKey(ofs, out localOutLen, out localBuf, out localBufOfs);
                if (localOutLen == 0) throw new BTDBException("Trying to read key outside of its boundary");
                Array.Copy(localBuf, localBufOfs, buf, bufOfs, Math.Min(len, localOutLen));
                ofs += localOutLen;
                bufOfs += localOutLen;
                len -= localOutLen;
            }
        }

        public void PeekValue(long ofs, out int len, out byte[] buf, out int bufOfs)
        {
            if (_currentKeyIndexInLeaf < 0) throw new BTDBException("Current Key is invalid");
            var iter = new BTreeChildIterator(_currentKeySector.Data);
            iter.MoveTo(_currentKeyIndexInLeaf);
            if (ofs < 0 || ofs >= iter.ValueLen)
            {
                len = 0;
                buf = null;
                bufOfs = 0;
                return;
            }
            if (ofs >= iter.ValueLen - iter.ValueLenInline)
            {
                len = (int)(iter.ValueLen - ofs);
                buf = _currentKeySector.Data;
                bufOfs = iter.ValueOffset + iter.ValueLenInline - len;
                return;
            }
            SectorPtr dataSectorPtr = iter.ValueSectorPtr;
            long dataLen = iter.ValueLen - iter.ValueLenInline;
            Sector parentOfSector = _currentKeySector;
            Debug.Assert(ofs < dataLen);
            var unlockStack = new List<Sector>();
            try
            {
                while (true)
                {
                    Sector dataSector = _owner.TryGetSector(dataSectorPtr.Ptr);
                    if (dataLen <= LowLevelDB.MaxLeafDataSectorSize)
                    {
                        if (dataSector == null)
                        {
                            dataSector = _owner.ReadSector(dataSectorPtr, IsWriteTransaction());
                            dataSector.Type = SectorType.DataChild;
                            dataSector.Parent = parentOfSector;
                        }
                        unlockStack.Add(dataSector);
                        buf = dataSector.Data;
                        bufOfs = (int)ofs;
                        len = (int)(dataSector.Length - ofs);
                        return;
                    }
                    if (dataSector == null)
                    {
                        dataSector = _owner.ReadSector(dataSectorPtr, IsWriteTransaction());
                        dataSector.Type = SectorType.DataParent;
                        dataSector.Parent = parentOfSector;
                    }
                    unlockStack.Add(dataSector);
                    parentOfSector = dataSector;
                    int downPtrCount;
                    long bytesInDownLevel = GetBytesInDownLevel(dataLen, out downPtrCount);
                    var i = (int)(ofs / bytesInDownLevel);
                    ofs = ofs % bytesInDownLevel;
                    dataSectorPtr = SectorPtr.Unpack(dataSector.Data, i * LowLevelDB.PtrDownSize);
                    if (i < downPtrCount - 1)
                    {
                        dataLen = bytesInDownLevel;
                    }
                    else
                    {
                        dataLen = dataLen % bytesInDownLevel;
                        if (dataLen == 0) dataLen = bytesInDownLevel;
                    }
                }
            }
            finally
            {
                unlockStack.ForEach(s => s.Unlock());
            }
        }

        public void ReadValue(long ofs, int len, byte[] buf, int bufOfs)
        {
            while (len > 0)
            {
                byte[] localBuf;
                int localBufOfs;
                int localOutLen;
                PeekValue(ofs, out localOutLen, out localBuf, out localBufOfs);
                if (localOutLen == 0) throw new BTDBException("Trying to read value outside of its boundary");
                Array.Copy(localBuf, localBufOfs, buf, bufOfs, Math.Min(len, localOutLen));
                ofs += localOutLen;
                bufOfs += localOutLen;
                len -= localOutLen;
            }
        }

        public void WriteValue(long ofs, int len, byte[] buf, int bufOfs)
        {
            if (len < 0) throw new ArgumentOutOfRangeException("len");
            if (ofs < 0) throw new ArgumentOutOfRangeException("ofs");
            if (len == 0) return;
            if (_currentKeyIndexInLeaf < 0) throw new BTDBException("Current Key is invalid");
            UpgradeToWriteTransaction();
            if (ofs + len > GetValueSize()) SetValueSize(ofs + len);
            InternalWriteValue(ofs, len, buf, bufOfs);
        }

        void InternalWriteValue(long ofs, int len, byte[] buf, int bufOfs)
        {
            _currentKeySector = _owner.DirtizeSector(_currentKeySector, _currentKeySector.Parent, _currentKeySectorParents);
            var iter = new BTreeChildIterator(_currentKeySector.Data);
            iter.MoveTo(_currentKeyIndexInLeaf);
            var valueLen = iter.ValueLen;
            var valueLenInline = iter.ValueLenInline;
            if (ofs + len > valueLen - valueLenInline)
            {
                var inlineEnd = (int)(ofs + len - (valueLen - valueLenInline));
                var inlineStart = 0;
                if (ofs > valueLen - valueLenInline)
                {
                    inlineStart = (int)(ofs - (valueLen - valueLenInline));
                }
                if (buf != null)
                {
                    var inlineBufOfs = bufOfs + (int)(valueLen - valueLenInline + inlineStart - ofs);
                    Array.Copy(buf, inlineBufOfs, iter.Data, iter.ValueOffset + inlineStart, inlineEnd - inlineStart);
                }
                else
                {
                    Array.Clear(iter.Data, iter.ValueOffset + inlineStart, inlineEnd - inlineStart);
                }
                len -= inlineEnd - inlineStart;
                if (len == 0) return;
            }
            iter.ValueSectorPtr = RecursiveWriteValue(iter.ValueSectorPtr, valueLen - valueLenInline, ofs, len, buf, bufOfs, _currentKeySector);
        }

        SectorPtr RecursiveWriteValue(SectorPtr sectorPtr, long valueLen, long ofs, int len, byte[] buf, int bufOfs, Sector newParent)
        {
            if (ofs < 0) throw new ArgumentOutOfRangeException("ofs");
            if (ofs + len > valueLen) throw new ArgumentOutOfRangeException("ofs");
            Sector dataSector = null;
            try
            {
                dataSector = _owner.TryGetSector(sectorPtr.Ptr);
                if (valueLen <= LowLevelDB.MaxLeafDataSectorSize)
                {
                    if (dataSector == null)
                    {
                        dataSector = _owner.ReadSector(sectorPtr, true);
                        dataSector.Type = SectorType.DataChild;
                    }
                    dataSector.Parent = newParent;
                    Debug.Assert(valueLen <= dataSector.Length);
                    dataSector = _owner.ResizeSectorNoUpdatePosition(dataSector, dataSector.Length, newParent, null); // DirtizeSector but without update position
                    if (buf != null)
                    {
                        Array.Copy(buf, bufOfs, dataSector.Data, (int)ofs, len);
                    }
                    else
                    {
                        Array.Clear(dataSector.Data, (int)ofs, len);
                    }
                    return dataSector.ToSectorPtr();
                }
                if (dataSector == null)
                {
                    dataSector = _owner.ReadSector(sectorPtr, true);
                    dataSector.Type = SectorType.DataParent;
                }
                dataSector.Parent = newParent;
                dataSector = _owner.DirtizeSector(dataSector, newParent, null);
                int downPtrCount;
                long bytesInDownLevel = GetBytesInDownLevel(valueLen, out downPtrCount);
                var i = (int)(ofs / bytesInDownLevel);
                while (i < downPtrCount)
                {
                    long newofs = ofs - i * bytesInDownLevel;
                    if (newofs + len <= 0) break;
                    long downValueLen;
                    if (i < downPtrCount - 1)
                    {
                        downValueLen = bytesInDownLevel;
                    }
                    else
                    {
                        downValueLen = valueLen % bytesInDownLevel;
                        if (downValueLen == 0) downValueLen = bytesInDownLevel;
                    }
                    SectorPtr downSectorPtr = SectorPtr.Unpack(dataSector.Data, i * LowLevelDB.PtrDownSize);
                    int newBufOfs = bufOfs;
                    int newlen = len;
                    if (newofs < 0)
                    {
                        newlen += (int)newofs;
                        newBufOfs -= (int)newofs;
                        newofs = 0;
                    }
                    if (downValueLen - newofs < newlen)
                    {
                        newlen = (int)(downValueLen - newofs);
                    }
                    downSectorPtr = RecursiveWriteValue(downSectorPtr, downValueLen, newofs, newlen, buf, newBufOfs, dataSector);
                    SectorPtr.Pack(dataSector.Data, i * LowLevelDB.PtrDownSize, downSectorPtr);
                    i++;
                }
                return dataSector.ToSectorPtr();
            }
            finally
            {
                if (dataSector != null) dataSector.Unlock();
            }
        }

        static long GetBytesInDownLevel(long len, out int downPtrCount)
        {
            if (len <= LowLevelDB.MaxLeafDataSectorSize)
            {
                downPtrCount = (int)len;
                return 1;
            }
            long leafSectors = len / LowLevelDB.MaxLeafDataSectorSize;
            if (len % LowLevelDB.MaxLeafDataSectorSize != 0) leafSectors++;
            long currentLevelLeafSectors = 1;
            while (currentLevelLeafSectors * LowLevelDB.MaxChildren < leafSectors)
                currentLevelLeafSectors *= LowLevelDB.MaxChildren;
            long bytesInDownLevel = currentLevelLeafSectors * LowLevelDB.MaxLeafDataSectorSize;
            downPtrCount = (int)((leafSectors + currentLevelLeafSectors - 1) / currentLevelLeafSectors);
            return bytesInDownLevel;
        }

        public void SetValueSize(long newSize)
        {
            if (newSize < 0) throw new ArgumentOutOfRangeException("newSize");
            if (_currentKeyIndexInLeaf < 0) throw new BTDBException("Current Key is invalid");
            var iter = new BTreeChildIterator(_currentKeySector.Data);
            iter.MoveTo(_currentKeyIndexInLeaf);
            long oldSize = iter.ValueLen;
            if (oldSize == newSize) return;
            UpgradeToWriteTransaction();
            int oldInlineSize = BTreeChildIterator.CalcValueLenInline(oldSize);
            int newInlineSize = BTreeChildIterator.CalcValueLenInline(newSize);
            var newEndContent = new byte[newInlineSize];
            byte[] oldEndContent = null;
            long newEndContentOfs = newSize - newEndContent.Length;
            if (oldSize < newSize)
            {
                oldEndContent = new byte[oldInlineSize];
                ReadValue(oldSize - oldInlineSize, oldInlineSize, oldEndContent, 0);
            }
            else
            {
                ReadValue(newEndContentOfs, (int)Math.Min(newEndContent.Length, oldSize - newEndContentOfs), newEndContent, 0);
            }
            long oldDeepSize = oldSize - oldInlineSize;
            long newDeepSize = newSize - newInlineSize;
            var oldValueSectorPtr = new SectorPtr();
            if (oldDeepSize > 0) oldValueSectorPtr = iter.ValueSectorPtr;
            _currentKeySector = _owner.ResizeSectorWithUpdatePosition(_currentKeySector, iter.TotalLength - iter.CurrentEntrySize + BTreeChildIterator.CalcEntrySize(iter.KeyLen, newSize), _currentKeySector.Parent, _currentKeySectorParents);
            iter.ResizeValue(_currentKeySector.Data, newSize);
            FixChildrenParentPointers(_currentKeySector);
            if (oldDeepSize != newDeepSize)
            {
                if (oldDeepSize == 0)
                {
                    SectorPtr.Pack(_currentKeySector.Data, iter.ValueOffset + newInlineSize,
                                   CreateContentSector(newDeepSize, _currentKeySector));
                }
                else if (newDeepSize == 0)
                {
                    DeleteContentSector(oldValueSectorPtr, oldDeepSize, _currentKeySector);
                }
                else
                {
                    SectorPtr.Pack(_currentKeySector.Data, iter.ValueOffset + newInlineSize,
                                   ResizeContentSector(oldValueSectorPtr, oldDeepSize, _currentKeySector, newDeepSize, null, 0));
                }
            }
            if (newEndContent.Length > 0) InternalWriteValue(newEndContentOfs, newEndContent.Length, newEndContent, 0);
            if (oldEndContent != null && oldEndContent.Length > 0) InternalWriteValue(oldSize - oldInlineSize, oldInlineSize, oldEndContent, 0);
        }

        public void SetValue(byte[] buf, int bufOfs, int len)
        {
            if (len < 0) throw new ArgumentOutOfRangeException("len");
            if (_currentKeyIndexInLeaf < 0) throw new BTDBException("Current Key is invalid");
            var iter = new BTreeChildIterator(_currentKeySector.Data);
            iter.MoveTo(_currentKeyIndexInLeaf);
            long oldSize = iter.ValueLen;
            if (oldSize == len)
            {
                WriteValue(0, len, buf, bufOfs);
                return;
            }
            UpgradeToWriteTransaction();
            int oldInlineSize = BTreeChildIterator.CalcValueLenInline(oldSize);
            int newInlineSize = BTreeChildIterator.CalcValueLenInline(len);
            long oldDeepSize = oldSize - oldInlineSize;
            int newDeepSize = len - newInlineSize;
            var oldValueSectorPtr = new SectorPtr();
            if (oldDeepSize > 0) oldValueSectorPtr = iter.ValueSectorPtr;
            _currentKeySector = _owner.ResizeSectorWithUpdatePosition(_currentKeySector, iter.TotalLength - iter.CurrentEntrySize + BTreeChildIterator.CalcEntrySize(iter.KeyLen, len), _currentKeySector.Parent, _currentKeySectorParents);
            iter.ResizeValue(_currentKeySector.Data, len);
            FixChildrenParentPointers(_currentKeySector);
            Array.Copy(buf, bufOfs + len - newInlineSize, _currentKeySector.Data, iter.ValueOffset, newInlineSize);
            if (oldDeepSize == 0)
            {
                if (newDeepSize != 0)
                {
                    SectorPtr.Pack(_currentKeySector.Data, iter.ValueOffset + newInlineSize,
                                    CreateContentSector(buf, bufOfs, newDeepSize, _currentKeySector));
                }
            }
            else if (newDeepSize == 0)
            {
                DeleteContentSector(oldValueSectorPtr, oldDeepSize, _currentKeySector);
            }
            else
            {
                SectorPtr.Pack(_currentKeySector.Data, iter.ValueOffset + newInlineSize,
                                ResizeContentSector(oldValueSectorPtr, oldDeepSize, _currentKeySector, newDeepSize, buf, bufOfs));
            }
        }

        SectorPtr ResizeContentSector(SectorPtr oldSectorPtr, long oldSize, Sector parentSector, long newSize, byte[] buf, int bufOfs)
        {
            Debug.Assert(oldSize != 0 && newSize != 0);
            Sector sector = null;
            if (oldSize == newSize)
            {
                if (buf != null)
                {
                    return RecursiveWriteValue(oldSectorPtr, newSize, 0, (int)newSize, buf, bufOfs, parentSector);
                }
                sector = _owner.TryGetSector(oldSectorPtr.Ptr);
                if (sector != null)
                {
                    sector.Parent = parentSector;
                    sector.Unlock();
                }
                return oldSectorPtr;
            }
            int oldDownPtrCount;
            var oldBytesInDownLevel = GetBytesInDownLevel(oldSize, out oldDownPtrCount);
            int newDownPtrCount;
            var newBytesInDownLevel = GetBytesInDownLevel(newSize, out newDownPtrCount);
            try
            {
                if (oldBytesInDownLevel < newBytesInDownLevel)
                {
                    sector = _owner.NewSector();
                    sector.SetLengthWithRound(newDownPtrCount * LowLevelDB.PtrDownSize);
                    sector.Parent = parentSector;
                    sector.Type = SectorType.DataParent;
                    _owner.PublishSector(sector);
                    SectorPtr.Pack(sector.Data, 0, ResizeContentSector(oldSectorPtr, oldSize, sector, newBytesInDownLevel, buf, bufOfs));
                    for (int i = 1; i < newDownPtrCount; i++)
                    {
                        long downLevelSize = Math.Min(newSize - i * newBytesInDownLevel, newBytesInDownLevel);
                        SectorPtr.Pack(sector.Data, i * LowLevelDB.PtrDownSize,
                                       buf != null
                                           ? CreateContentSector(buf, (int)(bufOfs + i * newBytesInDownLevel), (int)downLevelSize, sector)
                                           : CreateContentSector(downLevelSize, sector));
                    }
                    return sector.ToSectorPtr();
                }
                if (oldBytesInDownLevel > newBytesInDownLevel)
                {
                    sector = _owner.TryGetSector(oldSectorPtr.Ptr);
                    if (sector == null)
                    {
                        sector = _owner.ReadSector(oldSectorPtr, true);
                        sector.Type = SectorType.DataParent;
                        sector.Parent = parentSector;
                    }
                    for (int i = 1; i < oldDownPtrCount; i++)
                    {
                        long downLevelSize = Math.Min(oldSize - i * oldBytesInDownLevel, oldBytesInDownLevel);
                        DeleteContentSector(SectorPtr.Unpack(sector.Data, i * LowLevelDB.PtrDownSize), downLevelSize, sector);
                    }
                    _owner.DeallocateSector(sector);
                    return ResizeContentSector(SectorPtr.Unpack(sector.Data, 0), oldBytesInDownLevel, parentSector, newSize, buf, bufOfs);
                }
                if (oldBytesInDownLevel == 1)
                {
                    return ResizeContentSectorChild(ref sector, oldSectorPtr, oldDownPtrCount, newDownPtrCount, parentSector, buf, bufOfs);
                }
                sector = _owner.TryGetSector(oldSectorPtr.Ptr);
                if (sector == null)
                {
                    sector = _owner.ReadSector(oldSectorPtr, true);
                    sector.Type = SectorType.DataParent;
                }
                sector.Parent = parentSector;
                SectorPtr lastSectorPtr;
                long lastOffset;
                for (int i = newDownPtrCount + 1; i < oldDownPtrCount; i++)
                {
                    lastOffset = i * oldBytesInDownLevel;
                    lastSectorPtr = SectorPtr.Unpack(sector.Data, i * LowLevelDB.PtrDownSize);
                    DeleteContentSector(lastSectorPtr, Math.Min(oldSize - lastOffset, oldBytesInDownLevel), sector);
                }
                var lastCommonPtrCount = Math.Min(oldDownPtrCount, newDownPtrCount) - 1;
                byte[] oldData = sector.Data;
                sector = _owner.ResizeSectorNoUpdatePosition(sector, newDownPtrCount * LowLevelDB.PtrDownSize, parentSector, null);
                Array.Copy(oldData, 0, sector.Data, 0, (lastCommonPtrCount + 1) * LowLevelDB.PtrDownSize);
                if (buf != null)
                {
                    for (int i = 0; i < lastCommonPtrCount; i++)
                    {
                        lastSectorPtr = SectorPtr.Unpack(sector.Data, i * LowLevelDB.PtrDownSize);
                        lastOffset = i * newBytesInDownLevel;
                        lastSectorPtr = RecursiveWriteValue(lastSectorPtr, newBytesInDownLevel, 0, (int)newBytesInDownLevel, buf, (int)(bufOfs + lastOffset), sector);
                        SectorPtr.Pack(sector.Data, i * LowLevelDB.PtrDownSize, lastSectorPtr);
                    }
                }
                else
                {
                    for (int i = 0; i < lastCommonPtrCount; i++)
                    {
                        var lastSectorPos = PackUnpack.UnpackInt64(sector.Data, i * LowLevelDB.PtrDownSize);
                        _owner.FixChildParentPointer(lastSectorPos, sector);
                    }
                }
                lastSectorPtr = SectorPtr.Unpack(sector.Data, lastCommonPtrCount * LowLevelDB.PtrDownSize);
                lastOffset = lastCommonPtrCount * newBytesInDownLevel;
                lastSectorPtr = ResizeContentSector(lastSectorPtr, Math.Min(oldSize - lastOffset, oldBytesInDownLevel), sector, Math.Min(newSize - lastOffset, newBytesInDownLevel), buf, (int)(bufOfs + lastOffset));
                SectorPtr.Pack(sector.Data, lastCommonPtrCount * LowLevelDB.PtrDownSize, lastSectorPtr);
                for (int i = oldDownPtrCount; i < newDownPtrCount; i++)
                {
                    lastOffset = i * newBytesInDownLevel;
                    lastSectorPtr = buf != null ?
                        CreateContentSector(buf, (int)(bufOfs + lastOffset), (int)Math.Min(newSize - lastOffset, newBytesInDownLevel), sector) :
                        CreateContentSector(Math.Min(newSize - lastOffset, newBytesInDownLevel), sector);
                    SectorPtr.Pack(sector.Data, i * LowLevelDB.PtrDownSize, lastSectorPtr);
                }
                return sector.ToSectorPtr();
            }
            finally
            {
                if (sector != null) sector.Unlock();
            }
        }

        SectorPtr ResizeContentSectorChild(ref Sector sector, SectorPtr oldSectorPtr, int oldDownPtrCount, int newDownPtrCount, Sector parentSector, byte[] buf, int bufOfs)
        {
            sector = _owner.TryGetSector(oldSectorPtr.Ptr);
            if (sector == null)
            {
                sector = _owner.ReadSector(oldSectorPtr, true);
                sector.Type = SectorType.DataChild;
            }
            sector.Parent = parentSector;
            byte[] oldData = sector.Data;
            sector = _owner.ResizeSectorNoUpdatePosition(sector, newDownPtrCount, parentSector, null);
            if (buf == null)
            {
                Array.Copy(oldData, 0, sector.Data, 0, Math.Min(oldDownPtrCount, newDownPtrCount));
            }
            else
            {
                Array.Copy(buf, bufOfs, sector.Data, 0, newDownPtrCount);
            }
            return sector.ToSectorPtr();
        }

        public void EraseCurrent()
        {
            if (_currentKeyIndexInLeaf < 0) throw new BTDBException("Current Key is invalid");
            var relativeKeyIndex = _currentKeyIndex - _prefixKeyStart;
            EraseRange(relativeKeyIndex, relativeKeyIndex);
        }

        public void EraseAll()
        {
            EraseRange(0, long.MaxValue);
        }

        public void EraseRange(long firstKeyIndex, long lastKeyIndex)
        {
            if (firstKeyIndex < 0) firstKeyIndex = 0;
            if (lastKeyIndex >= GetKeyValueCount()) lastKeyIndex = _prefixKeyCount - 1;
            if (lastKeyIndex < firstKeyIndex) return;
            UpgradeToWriteTransaction();
            firstKeyIndex += _prefixKeyStart;
            lastKeyIndex += _prefixKeyStart;
            InvalidateCurrentKey();
            _prefixKeyCount -= lastKeyIndex - firstKeyIndex + 1;
            var rootBTree = GetRootBTreeSectorPtr();
            var sector = GetBTreeSector(rootBTree, null);
            try
            {
                if (firstKeyIndex == 0 && lastKeyIndex == (long)(_owner.NewState.KeyValuePairCount - 1))
                {
                    EraseCompletely(ref sector);
                }
                else
                {
                    ErasePartially(ref sector, firstKeyIndex, lastKeyIndex);
                    SimplifySingleSubChild(ref sector);
                }
            }
            finally
            {
                if (sector != null) sector.Unlock();
            }
            _owner.NewState.KeyValuePairCount -= (ulong)(lastKeyIndex - firstKeyIndex + 1);
            if (sector == null)
            {
                _owner.NewState.RootBTree.Ptr = 0;
                _owner.NewState.RootBTree.Checksum = 0;
                _owner.NewState.RootBTreeLevels = 0;
            }
            else
            {
                _owner.NewState.RootBTree = sector.ToSectorPtr();
            }
        }

        void SimplifySingleSubChild(ref Sector sector)
        {
            while (sector.Type == SectorType.BTreeParent)
            {
                var iter = new BTreeParentIterator(sector.Data);
                if (iter.Count > 0) break;
                var sectorPtr = iter.FirstChildSectorPtr;
                _owner.DeallocateSector(sector);
                sector.Unlock();
                // ReSharper disable RedundantAssignment
                sector = null;
                // ReSharper restore RedundantAssignment
                sector = GetBTreeSector(sectorPtr, null);
                _owner.NewState.RootBTreeLevels--;
            }
        }

        void EraseCompletely(ref Sector sector)
        {
            if (sector.Type == SectorType.BTreeChild)
            {
                var iter = new BTreeChildIterator(sector.Data);
                do
                {
                    if (iter.HasKeySectorPtr)
                        DeleteContentSector(iter.KeySectorPtr, iter.KeyLen - iter.KeyLenInline, sector);
                    if (iter.HasValueSectorPtr)
                        DeleteContentSector(iter.ValueSectorPtr, iter.ValueLen - iter.ValueLenInline, sector);
                } while (iter.MoveNext());
            }
            else
            {
                var iter = new BTreeParentIterator(sector.Data);
                for (int i = 0; i <= iter.Count; i++)
                {
                    var childSectorPtr = iter.GetChildSectorPtr(i);
                    Sector childSector = GetBTreeSector(childSectorPtr, sector);
                    try
                    {
                        EraseCompletely(ref childSector);
                    }
                    finally
                    {
                        if (childSector != null) childSector.Unlock();
                    }
                }
            }
            _owner.DeallocateSector(sector);
            sector.Unlock();
            sector = null;
        }

        void ErasePartially(ref Sector sector, long firstKeyIndex, long lastKeyIndex)
        {
            if (sector.Type == SectorType.BTreeParent)
            {
                ErasePartiallyParent(ref sector, firstKeyIndex, lastKeyIndex);
            }
            else
            {
                ErasePartiallyChild(ref sector, firstKeyIndex, lastKeyIndex);
            }
        }

        void ErasePartiallyParent(ref Sector sector, long firstKeyIndex, long lastKeyIndex)
        {
            sector = _owner.DirtizeSector(sector, sector.Parent, null);
            var iter = new BTreeParentIterator(sector.Data);
            int? firstChildErasedCompletely = null;
            int? lastChildErasedCompletely = null;
            int? firstChildErasedPartialy = null;

            if (firstKeyIndex < iter.FirstChildKeyCount)
            {
                firstChildErasedPartialy = 0;
                var childSectorPtr = iter.FirstChildSectorPtr;
                Sector childSector = GetBTreeSector(childSectorPtr, sector);
                try
                {
                    if (firstKeyIndex == 0 && lastKeyIndex + 1 >= iter.FirstChildKeyCount)
                    {
                        EraseCompletely(ref childSector);
                        firstChildErasedCompletely = 0;
                        lastChildErasedCompletely = 1;
                        lastKeyIndex -= iter.FirstChildKeyCount;
                    }
                    else
                    {
                        var removeCount = Math.Min(lastKeyIndex + 1, iter.FirstChildKeyCount) - firstKeyIndex;
                        ErasePartially(ref childSector, firstKeyIndex, firstKeyIndex + removeCount - 1);
                        firstKeyIndex = 0;
                        lastKeyIndex -= iter.FirstChildKeyCount;
                        iter.FirstChildSectorPtr = childSector.ToSectorPtr();
                        iter.FirstChildKeyCount -= removeCount;
                    }
                }
                finally
                {
                    if (childSector != null) childSector.Unlock();
                }
            }
            else
            {
                firstKeyIndex -= iter.FirstChildKeyCount;
                lastKeyIndex -= iter.FirstChildKeyCount;
            }
            for (int i = 1; i <= iter.Count; i++, iter.MoveNext())
            {
                if (lastKeyIndex < 0) break;
                var childKeyCount = iter.ChildKeyCount;
                if (firstKeyIndex >= childKeyCount)
                {
                    firstKeyIndex -= childKeyCount;
                    lastKeyIndex -= childKeyCount;
                    continue;
                }
                if (!firstChildErasedPartialy.HasValue) firstChildErasedPartialy = i;
                var childSectorPtr = iter.ChildSectorPtr;
                Sector childSector = GetBTreeSector(childSectorPtr, sector);
                try
                {
                    if (firstKeyIndex == 0 && lastKeyIndex + 1 >= childKeyCount)
                    {
                        EraseCompletely(ref childSector);
                        if (!firstChildErasedCompletely.HasValue) firstChildErasedCompletely = i;
                        lastChildErasedCompletely = i + 1;
                        lastKeyIndex -= childKeyCount;
                    }
                    else
                    {
                        var removeCount = Math.Min(lastKeyIndex + 1, childKeyCount) - firstKeyIndex;
                        ErasePartially(ref childSector, firstKeyIndex, firstKeyIndex + removeCount - 1);
                        iter.ChildSectorPtr = childSector.ToSectorPtr();
                        iter.ChildKeyCount -= removeCount;
                        if (firstKeyIndex == 0)
                        {
                            // update key because current key was removed
                            UpdateKeyAfterRemoval(ref sector, ref iter, childSector);
                            iter.MoveTo(i - 1);
                        }
                        lastKeyIndex -= childKeyCount;
                        firstKeyIndex = 0;
                    }
                }
                finally
                {
                    if (childSector != null) childSector.Unlock();
                }
            }
            if (firstChildErasedCompletely.HasValue)
            {
                InternalBTreeParentEraseRange(ref sector, ref iter, firstChildErasedCompletely.Value, lastChildErasedCompletely.Value);
            }
            SimplifyBTree(ref sector, firstChildErasedPartialy ?? 0);
        }

        void InternalBTreeParentEraseRange(ref Sector sector, ref BTreeParentIterator iter, int firstIndexToErase, int lastIndexToErase)
        {
            var originalLength = iter.TotalLength;
            Debug.Assert(iter.Count >= lastIndexToErase - firstIndexToErase);
            var eraseFromOfs = iter.OffsetOfIndex(firstIndexToErase);
            var eraseToOfs = lastIndexToErase - 1 == iter.Count
                                 ? originalLength + LowLevelDB.PtrDownSize + 8
                                 : iter.OffsetOfIndex(lastIndexToErase);
            sector = _owner.ResizeSectorNoUpdatePosition(sector,
                                                         originalLength - eraseToOfs + eraseFromOfs,
                                                         sector.Parent,
                                                         null);
            var newCount = iter.Count - (lastIndexToErase - firstIndexToErase);
            BTreeParentIterator.SetCountToSectorData(sector.Data, newCount);
            var ofs = BTreeParentIterator.HeaderSize + newCount * BTreeParentIterator.HeaderForEntry;
            if (firstIndexToErase == 0)
            {
                iter.MoveTo(lastIndexToErase - 1);
                Array.Copy(iter.Data, iter.ChildSectorPtrOffset, sector.Data, BTreeParentIterator.FirstChildSectorPtrOffset, LowLevelDB.PtrDownSize + 8);
                Array.Copy(iter.Data, eraseToOfs, sector.Data, ofs, originalLength - eraseToOfs);
            }
            else
            {
                Array.Copy(iter.Data, BTreeParentIterator.FirstChildSectorPtrOffset, sector.Data, BTreeParentIterator.FirstChildSectorPtrOffset, LowLevelDB.PtrDownSize + 8);
                iter.MoveTo(firstIndexToErase - 1);
                if (lastIndexToErase - 1 == iter.Count)
                {
                    Array.Copy(iter.Data, iter.FirstOffset, sector.Data, ofs, iter.EntryOffset - iter.FirstOffset);
                }
                else
                {
                    var ofs2 = ofs + iter.ChildSectorPtrOffset - iter.FirstOffset;
                    iter.MoveTo(lastIndexToErase - 1);
                    Array.Copy(iter.Data, iter.FirstOffset, sector.Data, ofs, eraseFromOfs - iter.FirstOffset);
                    ofs += eraseFromOfs - iter.FirstOffset;
                    Array.Copy(iter.Data, iter.ChildSectorPtrOffset, sector.Data, ofs2, LowLevelDB.PtrDownSize + 8);
                    Array.Copy(iter.Data, eraseToOfs, sector.Data, ofs, originalLength - eraseToOfs);
                }
            }
            BTreeParentIterator.RecalculateHeader(sector.Data, newCount);
        }

        void SimplifyBTree(ref Sector sector, int mergeAroundIndex)
        {
            var iter = new BTreeParentIterator(sector.Data);
            if (iter.Count == 0 || mergeAroundIndex > iter.Count)
                return;
            var lenCurrent = ApproximateLengthOfBTreeChild(iter.GetChildSectorPtr(mergeAroundIndex));
            var lenPrevious = -1;
            if (mergeAroundIndex > 0)
                lenPrevious = ApproximateLengthOfBTreeChild(iter.GetChildSectorPtr(mergeAroundIndex - 1));
            var lenNext = -1;
            if (mergeAroundIndex < iter.Count)
                lenNext = ApproximateLengthOfBTreeChild(iter.GetChildSectorPtr(mergeAroundIndex + 1));
            ShouldMergeResult result = _owner.Tweaks.ShouldMergeBTreeParent(lenPrevious, lenCurrent, lenNext);
            if (result == ShouldMergeResult.NoMerge)
                return;
            if (result == ShouldMergeResult.MergeWithPrevious)
                mergeAroundIndex--;
            long mergedPairs;
            var leftSectorPtr = iter.GetChildSectorPtrWithKeyCount(mergeAroundIndex, out mergedPairs);
            Sector leftSector = null;
            Sector rightSector = null;
            Sector mergedSector = null;
            try
            {
                leftSector = GetBTreeSector(leftSectorPtr, sector);
                long tempPairs;
                var rightSectorPtr = iter.GetChildSectorPtrWithKeyCount(mergeAroundIndex + 1, out tempPairs);
                mergedPairs += tempPairs;
                rightSector = GetBTreeSector(rightSectorPtr, sector);
                Debug.Assert(leftSector.Type == rightSector.Type);
                iter.MoveTo(mergeAroundIndex);
                if (leftSector.Type == SectorType.BTreeChild)
                {
                    var leftIter = new BTreeChildIterator(leftSector.Data);
                    var rightIter = new BTreeChildIterator(rightSector.Data);
                    if (!_owner.Tweaks.ShouldMerge2BTreeChild(leftIter.Count, leftIter.TotalLength, rightIter.Count,
                                                         rightIter.TotalLength))
                        return;
                    mergedSector = _owner.NewSector();
                    mergedSector.Type = SectorType.BTreeChild;
                    mergedSector.Parent = sector;
                    mergedSector.SetLengthWithRound(leftIter.TotalLength + rightIter.TotalLength - BTreeChildIterator.HeaderSize);
                    _owner.PublishSector(mergedSector);
                    var mergedCount = leftIter.Count + rightIter.Count;
                    BTreeChildIterator.SetCountToSectorData(mergedSector.Data, mergedCount);
                    var ofs = BTreeChildIterator.HeaderSize + BTreeChildIterator.HeaderForEntry * mergedCount;
                    Array.Copy(leftIter.Data, leftIter.FirstOffset, mergedSector.Data, ofs, leftIter.TotalLength - leftIter.FirstOffset);
                    ofs += leftIter.TotalLength - leftIter.FirstOffset;
                    Array.Copy(rightIter.Data, rightIter.FirstOffset, mergedSector.Data, ofs, rightIter.TotalLength - rightIter.FirstOffset);
                    BTreeChildIterator.RecalculateHeader(mergedSector.Data, mergedCount);
                }
                else
                {
                    var keyStorageLen = 4 + iter.KeyLenInline + (iter.HasKeySectorPtr ? LowLevelDB.PtrDownSize : 0);
                    var leftIter = new BTreeParentIterator(leftSector.Data);
                    var rightIter = new BTreeParentIterator(rightSector.Data);
                    if (!_owner.Tweaks.ShouldMerge2BTreeParent(leftIter.Count, leftIter.TotalLength, rightIter.Count,
                                                         rightIter.TotalLength, keyStorageLen))
                        return;
                    mergedSector = _owner.NewSector();
                    mergedSector.Type = SectorType.BTreeParent;
                    mergedSector.Parent = sector;
                    mergedSector.SetLengthWithRound(leftIter.TotalLength + rightIter.TotalLength + keyStorageLen);
                    _owner.PublishSector(mergedSector);
                    var mergedCount = leftIter.Count + rightIter.Count + 1;
                    BTreeParentIterator.SetCountToSectorData(mergedSector.Data, mergedCount);
                    Array.Copy(leftIter.Data, BTreeParentIterator.FirstChildSectorPtrOffset, mergedSector.Data,
                               BTreeParentIterator.FirstChildSectorPtrOffset, LowLevelDB.PtrDownSize + 8);
                    var ofs = BTreeParentIterator.HeaderSize + BTreeParentIterator.HeaderForEntry * mergedCount;
                    Array.Copy(leftIter.Data, leftIter.FirstOffset, mergedSector.Data, ofs, leftIter.TotalLength - leftIter.FirstOffset);
                    ofs += leftIter.TotalLength - leftIter.FirstOffset;
                    PackUnpack.PackInt32(mergedSector.Data, ofs, iter.KeyLen);
                    ofs += 4;
                    Array.Copy(rightIter.Data, BTreeParentIterator.FirstChildSectorPtrOffset, mergedSector.Data, ofs, LowLevelDB.PtrDownSize + 8);
                    ofs += LowLevelDB.PtrDownSize + 8;
                    Array.Copy(iter.Data, iter.KeyOffset, mergedSector.Data, ofs, keyStorageLen - 4);
                    ofs += keyStorageLen - 4;
                    Array.Copy(rightIter.Data, rightIter.FirstOffset, mergedSector.Data, ofs, rightIter.TotalLength - rightIter.FirstOffset);
                    BTreeParentIterator.RecalculateHeader(mergedSector.Data, mergedCount);
                }
                FixChildrenParentPointers(mergedSector);
                _owner.DeallocateSector(leftSector);
                InternalBTreeParentEraseRange(ref sector, ref iter, mergeAroundIndex, mergeAroundIndex + 1);
                new BTreeParentIterator(sector.Data).SetChildSectorPtrWithKeyCount(mergeAroundIndex,
                                                                                   mergedSector.ToSectorPtr(),
                                                                                   mergedPairs);
                _owner.DeallocateSector(rightSector);
            }
            finally
            {
                if (leftSector != null) leftSector.Unlock();
                if (rightSector != null) rightSector.Unlock();
                if (mergedSector != null) mergedSector.Unlock();
            }
        }

        int ApproximateLengthOfBTreeChild(SectorPtr childSectorPtr)
        {
            if (childSectorPtr.Ptr >= 0)
            {
                return (int)(childSectorPtr.Ptr & LowLevelDB.MaskOfGranLength) * LowLevelDB.AllocationGranularity;
            }
            Sector child = _owner.TryGetSector(childSectorPtr.Ptr);
            try
            {
                return child.Length;
            }
            finally
            {
                child.Unlock();
            }
        }

        void UpdateKeyAfterRemoval(ref Sector sector, ref BTreeParentIterator iter, Sector childSector)
        {
            var oldKeyStorageLen = iter.NextEntryOffset - iter.EntryOffset;
            byte[] data;
            int ofs;
            int len;
            ExtractFirstKey(childSector, out data, out ofs, out len);
            // structure of data is keylen/4, valuelen/8, inlinekey/var, [downptr/12]
            int originalLength = iter.TotalLength;
            sector = _owner.ResizeSectorNoUpdatePosition(sector,
                                                         originalLength - oldKeyStorageLen + len + LowLevelDB.PtrDownSize,
                                                         sector.Parent,
                                                         null);
            Array.Copy(iter.Data, 0, sector.Data, 0, iter.EntryOffset);
            Array.Copy(data, ofs, sector.Data, iter.EntryOffset, 4);
            Array.Copy(iter.Data, iter.EntryOffset + 4, sector.Data, iter.EntryOffset + 4, LowLevelDB.PtrDownSize + 8);
            Array.Copy(iter.Data, iter.NextEntryOffset, sector.Data, iter.EntryOffset + len + LowLevelDB.PtrDownSize, originalLength - iter.NextEntryOffset);
            Array.Copy(data, ofs + 12, sector.Data, iter.KeyOffset, len - 12);
            iter = new BTreeParentIterator(sector.Data);
        }

        void ExtractFirstKey(Sector sector, out byte[] data, out int ofs, out int len)
        {
            if (sector.Type == SectorType.BTreeChild)
            {
                var iter = new BTreeChildIterator(sector.Data);
                data = iter.Data;
                ofs = iter.EntryOffset;
                len = iter.ValueOffset - ofs;
                if (iter.HasKeySectorPtr) ForceKeyFlush(iter.KeySectorPtr, iter.KeyLen - iter.KeyLenInline, sector);
                Debug.Assert(data == sector.Data);
                return;
            }
            else
            {
                var iter = new BTreeParentIterator(sector.Data);
                var childSector = GetBTreeSector(iter.FirstChildSectorPtr, sector);
                try
                {
                    ExtractFirstKey(childSector, out data, out ofs, out len);
                }
                finally
                {
                    childSector.Unlock();
                }
            }
        }

        Sector GetBTreeSector(SectorPtr childSectorPtr, Sector parent)
        {
            var sector = _owner.TryGetSector(childSectorPtr.Ptr);
            if (sector != null)
            {
                sector.Parent = parent;
                return sector;
            }
            sector = _owner.ReadSector(childSectorPtr, true);
            sector.Parent = parent;
            sector.Type = BTreeChildIterator.IsChildFromSectorData(sector.Data)
                                    ? SectorType.BTreeChild
                                    : SectorType.BTreeParent;
            return sector;
        }

        void ErasePartiallyChild(ref Sector sector, long firstKeyIndex, long lastKeyIndex)
        {
            var iter = new BTreeChildIterator(sector.Data);
            iter.MoveTo((int)firstKeyIndex);
            var eraseFromOfs = iter.EntryOffset;
            while (true)
            {
                if (iter.HasKeySectorPtr)
                    DeleteContentSector(iter.KeySectorPtr, iter.KeyLen - iter.KeyLenInline, sector);
                if (iter.HasValueSectorPtr)
                    DeleteContentSector(iter.ValueSectorPtr, iter.ValueLen - iter.ValueLenInline, sector);
                if (iter.Index == (int)lastKeyIndex) break;
                iter.MoveNext();
            }
            var eraseToOfs = iter.EntryOffset + iter.CurrentEntrySize;
            var originalLength = iter.TotalLength;
            var newCount = iter.Count - 1 - (int)(lastKeyIndex - firstKeyIndex);
            sector = _owner.ResizeSectorNoUpdatePosition(sector,
                                                         originalLength - eraseToOfs + eraseFromOfs - BTreeChildIterator.HeaderForEntry * (iter.Count - newCount),
                                                         sector.Parent,
                                                         null);
            BTreeChildIterator.SetCountToSectorData(sector.Data, newCount);
            var ofs = BTreeChildIterator.HeaderSize + BTreeChildIterator.HeaderForEntry * newCount;
            Array.Copy(iter.Data, iter.FirstOffset, sector.Data, ofs, eraseFromOfs - iter.FirstOffset);
            ofs += eraseFromOfs - iter.FirstOffset;
            Array.Copy(iter.Data, eraseToOfs, sector.Data, ofs, originalLength - eraseToOfs);
            BTreeChildIterator.RecalculateHeader(sector.Data, newCount);
        }

        public void Commit()
        {
            InvalidateCurrentKey();
            if (_readLink != null) return; // It is read only transaction nothing to commit
            _owner.CommitWriteTransaction();
        }

        public LowLevelDBStats CalculateStats()
        {
            return _owner.CalculateStats(_readLink);
        }
    }
}
