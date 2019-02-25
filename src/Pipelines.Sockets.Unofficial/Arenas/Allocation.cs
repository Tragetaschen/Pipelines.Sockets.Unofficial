﻿using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Pipelines.Sockets.Unofficial.Arenas
{
    /// <summary>
    /// Represents an Allocation-T without needing to know the T at compile-time
    /// </summary>
    public readonly struct Allocation
    {
        private readonly int _offset, _length;
        private readonly IBlock _block;

        /// <summary>
        /// Indicates the type of element defined the allocation
        /// </summary>
        public Type ElementType
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _block?.ElementType ?? typeof(void);
        }

        /// <summary>
        /// Converts an untyped allocation back to a typed allocation; the type must be correct
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Allocation<T> Cast<T>()
            => _block is NilBlock ? TypeCheckedDefault<T>() : new Allocation<T>((Block<T>)_block, _offset, _length);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        Allocation<T> TypeCheckedDefault<T>()
        {
            GC.KeepAlive((NilBlock<T>)_block); // null (default) or correct
            return default;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Allocation(IBlock block, int offset, int length)
        {
            _block = block;
            _offset = offset;
            _length = length;
        }
    }

    /// <summary>
    /// Represents a (possibly non-contiguous) region of memory; the read/write cousin or ReadOnlySequence-T
    /// </summary>
    public readonly struct Allocation<T>
    {
        private readonly int _offsetAndMultiSegmentFlag, _length;
        private readonly Block<T> _block;

        /// <summary>
        /// Represents a typed allocation as an untyped allocation
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator Allocation(Allocation<T> allocation)
            => allocation.Untyped();

        /// <summary>
        /// Converts an untyped allocation back to a typed allocation; the type must be correct
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static explicit operator Allocation<T>(Allocation allocation)
            => allocation.Cast<T>();

        /// <summary>
        /// Converts a typed allocation to a typed read-only-sequence
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator ReadOnlySequence<T>(Allocation<T> allocation)
            => allocation.AsReadOnly();

        /// <summary>
        /// Represents a typed allocation as an untyped allocation
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Allocation Untyped() => new Allocation(_block ?? NilBlock<T>.Default, Offset, _length);

        /// <summary>
        /// Converts a typed allocation to a typed read-only-sequence
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySequence<T> AsReadOnly()
            => IsEmpty ? default
            : IsSingleSegment ? new ReadOnlySequence<T>(_block, Offset, _block, Offset + _length) : MultiSegmentAsReadOnly();

        /// <summary>
        /// Obtains a sub-region of an allocation
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Allocation<T> Slice(int start)
        {
            // does the start fit into the first block?
            int newStart;
            if ((_length != 0 & start >= 0) && (newStart = Offset + start) < _block.Length)
                return new Allocation<T>(_block, newStart, _length - start);
            return SlowSlice(start, _length - start);
        }

        /// <summary>
        /// Obtains a sub-region of an allocation
        /// </summary>
        public Allocation<T> Slice(int start, int length)
        {
            // does the start fit into the first block and still well-defined?
            int newStart;
            if ((_length != 0 & start >= 0 & length >= 0 & (start + length <= _length)) && (newStart = Offset + start) < _block.Length)
                return new Allocation<T>(_block, newStart, length);
            return SlowSlice(start, _length - start);
        }

        private Allocation<T> SlowSlice(int start, int length)
        {
            if (start < 0 | start > _length) ThrowArgumentOutOfRange(nameof(start));
            if (length < 0 | start + length > _length) ThrowArgumentOutOfRange(nameof(length));

            // note that this can be a zero length range that preserves the block, for SequencePosition purposes
            return new Allocation<T>(_block, Offset + start, length);

            void ThrowArgumentOutOfRange(string paramName) => throw new ArgumentOutOfRangeException(paramName);
        }

        /// <summary>
        /// Attempts to convert a typed read-only-sequence back to a typed allocation; the sequence must have originated from a valid typed allocation
        /// </summary>
        public static bool TryGetAllocation(ReadOnlySequence<T> sequence, out Allocation<T> allocation)
        {
            if (sequence.IsEmpty)
            {
                allocation = default;
                return true;
            }
            SequencePosition start = sequence.Start;
            if(start.GetObject() is Block<T> startBlock && sequence.End.GetObject() is Block<T>)
            {
                allocation = new Allocation<T>(startBlock, start.GetInteger(), checked((int)sequence.Length));
                return true;
            }
            allocation = default;
            return false;

        }

        private ReadOnlySequence<T> MultiSegmentAsReadOnly()
        {
            var start = _block;
            var startIndex = Offset;

            var current = start.Next;
            var remaining = _length - startIndex;
            while(current.Length < remaining)
            {
                remaining -= current.Length;
                current = current.Next;
            }
            return new ReadOnlySequence<T>(start, startIndex, current, remaining);
        }

        /// <summary>
        /// Indicates the number of elements in the allocation
        /// </summary>
        public long Length => _length; // we currently only allow int, but technically we could support huge blocks

        /// <summary>
        /// Indicates whether the allocation involves multiple segments, vs whether all the data fits into the first segment
        /// </summary>
        public bool IsSingleSegment
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (_offsetAndMultiSegmentFlag & MSB) == 0;
        }

        private const int MSB = unchecked((int)(uint)0x80000000);

        private int Offset
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _offsetAndMultiSegmentFlag & ~MSB;
        }

        /// <summary>
        /// Indicates whether the allocation is empty (zero elements)
        /// </summary>
        public bool IsEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _length == 0;
        }

        /// <summary>
        /// Obtains the first segment, in terms of a memory
        /// </summary>
        public Memory<T> FirstSegment
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _block == null ? default :
                IsSingleSegment ? _block.Memory.Slice(_offsetAndMultiSegmentFlag, _length) : _block.Memory.Slice(Offset);
        }
        /// <summary>
        /// Obtains the first segment, in terms of a span
        /// </summary>
        public Span<T> FirstSpan
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _block == null ? default :
                IsSingleSegment ? _block.Memory.Span.Slice(_offsetAndMultiSegmentFlag, _length) : _block.Memory.Span.Slice(Offset);
        }

        /// <summary>
        /// Copy the contents of the allocation into a contiguous region
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void CopyTo(Span<T> destination)
        {
            if (IsSingleSegment) FirstSpan.CopyTo(destination);
            else if (!TrySlowCopy(destination)) ThrowLengthError();

            void ThrowLengthError()
            {
                Span<int> one = stackalloc int[1];
                one.CopyTo(default); // this should give use the CLR's error text (let's hope it doesn't mention sizes!)
            }
        }

        /// <summary>
        /// If possible, copy the contents of the allocation into a contiguous region
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryCopyTo(Span<T> destination)
            => IsSingleSegment ? FirstSpan.TryCopyTo(destination) : TrySlowCopy(destination);

        private bool TrySlowCopy(Span<T> destination)
        {
            if (destination.Length < _length) return false;

            foreach(var span in Spans)
            {
                span.CopyTo(destination);
                destination = destination.Slice(span.Length);
            }
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Allocation(Block<T> block, int offset, int length)
        {
            Debug.Assert(block != null, "block should never be null");
            Debug.Assert(length >= 0, "block should not be negative");
            Debug.Assert(offset >= 0, "offset should not be negative");

            _block = block;
            _offsetAndMultiSegmentFlag = ((offset + length) > block.Length) ? (offset | MSB) : offset;
            _length = length;
            
        }

        /// <summary>
        /// Allows an allocation to be enumerated as spans
        /// </summary>
        public SpanEnumerable Spans => new SpanEnumerable(this);

        /// <summary>
        /// Allows an allocation to be enumerated as memory instances
        /// </summary>
        public MemoryEnumerable Segments => new MemoryEnumerable(this);

        /// <summary>
        /// Allows an allocation to be enumerated as spans
        /// </summary>
        public readonly ref struct SpanEnumerable
        {
            private readonly int _offset, _length;
            private readonly Block<T> _block;
            internal SpanEnumerable(in Allocation<T> allocation)
            {
                _offset = allocation.Offset;
                _length = allocation._length;
                _block = allocation._block;
            }

            /// <summary>
            /// Allows an allocation to be enumerated as spans
            /// </summary>
            public SpanEnumerator GetEnumerator() => new SpanEnumerator(_block, _offset, _length);
        }

        /// <summary>
        /// Allows an allocation to be enumerated as memory instances
        /// </summary>
        public readonly ref struct MemoryEnumerable
        {
            private readonly int _offset, _length;
            private readonly Block<T> _block;
            internal MemoryEnumerable(in Allocation<T> allocation)
            {
                _offset = allocation.Offset;
                _length = allocation._length;
                _block = allocation._block;
            }

            /// <summary>
            /// Allows an allocation to be enumerated as memory instances
            /// </summary>
            public MemoryEnumerator GetEnumerator() => new MemoryEnumerator(_block, _offset, _length);
        }

        public IndexerEnumerable Indexer => new IndexerEnumerable(this);
        public readonly ref struct IndexerEnumerable
        {
            private readonly int _offset, _length;
            private readonly Block<T> _block;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal IndexerEnumerable(in Allocation<T> allocation)
            {
                _offset = allocation.Offset;
                _length = allocation._length;
                _block = allocation._block;
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public IndexerEnumerator GetEnumerator() => new IndexerEnumerator(_block, _offset, _length);
        }

        public ref struct IndexerEnumerator
        {
            private int _remainingThisSpan, _offsetThisSpan, _remainingOtherBlocks;
            private Block<T> _nextBlock;
            private Span<T> _span;

            internal IndexerEnumerator(Block<T> block, int offset, int length)
            {
                var firstSpan = block.Memory.Span;
                if (offset + length > firstSpan.Length)
                {
                    // multi-block
                    _nextBlock = block.Next;
                    _remainingThisSpan = firstSpan.Length - offset;
                    _span = firstSpan.Slice(offset, _remainingThisSpan);
                    _remainingOtherBlocks = length - _remainingThisSpan;
                }
                else
                {
                    // single-block
                    _nextBlock = null;
                    _remainingThisSpan = length;
                    _span = firstSpan.Slice(offset);
                    _remainingOtherBlocks = 0;
                }
                _offsetThisSpan = -1;
            }

            /// <summary>
            /// Attempt to move the next value
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool MoveNext()
            {
                if (_remainingThisSpan == 0) return MoveNextBlock();
                _offsetThisSpan++;
                _remainingThisSpan--;
                return true;
            }

            private bool MoveNextBlock()
            {
                if (_remainingOtherBlocks == 0) return false;

                var span = _nextBlock.Memory.Span;
                _nextBlock = _nextBlock.Next;

                if (_remainingOtherBlocks <= span.Length)
                {   // we're at the end
                    span = span.Slice(0, _remainingOtherBlocks);
                    _remainingOtherBlocks = 0;
                }
                else
                {
                    _remainingOtherBlocks -= span.Length;
                }
                _span = span;
                _remainingThisSpan = span.Length - 1; // because we're consuming one
                _offsetThisSpan = 0;
                return true;
            }

            /// <summary>
            /// Obtain the current value
            /// </summary>
            public T Current => _span[_offsetThisSpan];
        }

        public RefAddEnumerable RefAdd => new RefAddEnumerable(this);
        public readonly ref struct RefAddEnumerable
        {
            private readonly int _offset, _length;
            private readonly Block<T> _block;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal RefAddEnumerable(in Allocation<T> allocation)
            {
                _offset = allocation.Offset;
                _length = allocation._length;
                _block = allocation._block;
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public RefAddEnumerator GetEnumerator() => new RefAddEnumerator(_block, _offset, _length);
        }

        public ref struct RefAddEnumerator
        {
            private int _remainingThisSpan, _offsetThisSpan, _remainingOtherBlocks;
            private Block<T> _nextBlock;
            private Span<T> _span;

            internal RefAddEnumerator(Block<T> block, int offset, int length)
            {
                var firstSpan = block.Memory.Span;
                if (offset + length > firstSpan.Length)
                {
                    // multi-block
                    _nextBlock = block.Next;
                    _remainingThisSpan = firstSpan.Length - offset;
                    _span = firstSpan.Slice(offset, _remainingThisSpan);
                    _remainingOtherBlocks = length - _remainingThisSpan;
                }
                else
                {
                    // single-block
                    _nextBlock = null;
                    _remainingThisSpan = length;
                    _span = firstSpan.Slice(offset);
                    _remainingOtherBlocks = 0;
                }
                _offsetThisSpan = -1;
            }

            /// <summary>
            /// Attempt to move the next value
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool MoveNext()
            {
                if (_remainingThisSpan == 0) return MoveNextBlock();
                _offsetThisSpan++;
                _remainingThisSpan--;
                return true;
            }

            private bool MoveNextBlock()
            {
                if (_remainingOtherBlocks == 0) return false;

                var span = _nextBlock.Memory.Span;
                _nextBlock = _nextBlock.Next;

                if (_remainingOtherBlocks <= span.Length)
                {   // we're at the end
                    span = span.Slice(0, _remainingOtherBlocks);
                    _remainingOtherBlocks = 0;
                }
                else
                {
                    _remainingOtherBlocks -= span.Length;
                }
                _span = span;
                _remainingThisSpan = span.Length - 1; // because we're consuming one
                _offsetThisSpan = 0;
                return true;
            }

            /// <summary>
            /// Obtain the current value
            /// </summary>
            public T Current
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => Unsafe.Add(ref MemoryMarshal.GetReference(_span), _offsetThisSpan);
            }
        }

        /// <summary>
        /// Allows an allocation to be enumerated as spans
        /// </summary>
        public ref struct SpanEnumerator
        {
            private int _offset, _remaining;
            private Block<T> _nextBlock;
            private Span<T> _current;

            internal SpanEnumerator(Block<T> block, int offset, int length)
            {
                _nextBlock = block;
                _offset = offset;
                _remaining = length;
                _current = default;
            }

            /// <summary>
            /// Attempt to move the next block
            /// </summary>
            public bool MoveNext()
            {
                if (_remaining == 0) return false;
                var span = _nextBlock.Memory.Span;
                _nextBlock = _nextBlock.Next;

                if (_remaining <= span.Length - _offset)
                {
                    // last block; need to trim end
                    span = span.Slice(_offset, _remaining);
                }
                else if (_offset != 0)
                {
                    // has offset (first only)
                    span = span.Slice(_offset);
                    _offset = 0;
                }
                // otherwise we can take the entire thing
                _remaining -= span.Length;
                _current = span;
                return true;
            }

            /// <summary>
            /// Obtain the current block
            /// </summary>
            public Span<T> Current => _current;
        }

        /// <summary>
        /// Allows an allocation to be enumerated as memory instances
        /// </summary>
        public struct MemoryEnumerator
        {
            private int _offset, _remaining;
            private Block<T> _nextBlock;
            private Memory<T> _current;

            internal MemoryEnumerator(Block<T> block, int offset, int length)
            {
                _nextBlock = block;
                _offset = offset;
                _remaining = length;
                _current = default;
            }

            /// <summary>
            /// Attempt to move the next block
            /// </summary>
            public bool MoveNext()
            {
                if (_remaining == 0) return false;
                var memory = _nextBlock.Memory;
                _nextBlock = _nextBlock.Next;

                if (_remaining <= memory.Length - _offset)
                {
                    // last block; need to trim end
                    memory = memory.Slice(_offset, _remaining);
                }
                else if (_offset != 0)
                {
                    // has offset (first only)
                    memory = memory.Slice(_offset);
                    _offset = 0;
                }
                // otherwise we can take the entire thing
                _remaining -= memory.Length;
                _current = memory;
                return true;
            }

            /// <summary>
            /// Obtain the current block
            /// </summary>
            public Memory<T> Current => _current;
        }
    }
}
