package runner

import (
	"sync"
)

// BoundedBuffer is an io.Writer that stops appending once it has accepted
// `cap` bytes total, but reports the count of bytes the caller asked it to
// write so the demuxer can keep its frame-position alignment.
//
// After the cap is reached, Truncated() returns true. Bytes() returns the
// captured prefix.
type BoundedBuffer struct {
	mu        sync.Mutex
	cap       int64
	buf       []byte
	bytesWrit int64
	truncated bool
}

// NewBoundedBuffer constructs a buffer that captures up to capBytes bytes.
// capBytes <= 0 means unlimited (used in tests; production uses real caps).
func NewBoundedBuffer(capBytes int64) *BoundedBuffer {
	return &BoundedBuffer{cap: capBytes, buf: make([]byte, 0, min64(capBytes, 4096))}
}

// Write returns len(p) regardless of how many bytes were actually appended,
// so the docker log demuxer's frame accounting stays correct. Use Truncated()
// to detect the cap hit.
func (b *BoundedBuffer) Write(p []byte) (int, error) {
	b.mu.Lock()
	defer b.mu.Unlock()
	if b.cap > 0 {
		remaining := b.cap - b.bytesWrit
		if remaining <= 0 {
			b.bytesWrit += int64(len(p))
			b.truncated = true
			return len(p), nil
		}
		take := int64(len(p))
		if take > remaining {
			take = remaining
			b.truncated = true
		}
		b.buf = append(b.buf, p[:take]...)
		b.bytesWrit += int64(len(p))
		return len(p), nil
	}
	b.buf = append(b.buf, p...)
	b.bytesWrit += int64(len(p))
	return len(p), nil
}

// Bytes returns the captured prefix.
func (b *BoundedBuffer) Bytes() []byte {
	b.mu.Lock()
	defer b.mu.Unlock()
	out := make([]byte, len(b.buf))
	copy(out, b.buf)
	return out
}

// String is the captured prefix as a UTF-8 string.
func (b *BoundedBuffer) String() string { return string(b.Bytes()) }

// Truncated reports whether at least one byte was discarded.
func (b *BoundedBuffer) Truncated() bool {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.truncated
}

// BytesWritten returns the total of bytes the caller passed to Write,
// including bytes that were dropped due to the cap.
func (b *BoundedBuffer) BytesWritten() int64 {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.bytesWrit
}

func min64(a, b int64) int64 {
	if a == 0 {
		return b
	}
	if a < b {
		return a
	}
	return b
}
