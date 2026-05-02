package runner

import "testing"

func TestBoundedBuffer_NoCap(t *testing.T) {
	b := NewBoundedBuffer(0)
	n, _ := b.Write([]byte("hello"))
	if n != 5 || b.String() != "hello" || b.Truncated() {
		t.Fatalf("got n=%d s=%q trunc=%v", n, b.String(), b.Truncated())
	}
}

func TestBoundedBuffer_CapWithinFirstWrite(t *testing.T) {
	b := NewBoundedBuffer(3)
	n, _ := b.Write([]byte("hello"))
	if n != 5 {
		t.Fatalf("n: %d (must equal len(p) for demux alignment)", n)
	}
	if got := b.String(); got != "hel" {
		t.Fatalf("captured: %q", got)
	}
	if !b.Truncated() {
		t.Fatal("Truncated should be true after cap hit")
	}
	if b.BytesWritten() != 5 {
		t.Fatalf("BytesWritten: %d", b.BytesWritten())
	}
}

func TestBoundedBuffer_CapAcrossWrites(t *testing.T) {
	b := NewBoundedBuffer(5)
	b.Write([]byte("abc"))
	b.Write([]byte("def"))
	b.Write([]byte("ghi"))
	if b.String() != "abcde" {
		t.Fatalf("captured: %q", b.String())
	}
	if !b.Truncated() {
		t.Fatal("Truncated must be true")
	}
	if b.BytesWritten() != 9 {
		t.Fatalf("BytesWritten: %d", b.BytesWritten())
	}
}

func TestBoundedBuffer_ExactCap(t *testing.T) {
	b := NewBoundedBuffer(5)
	b.Write([]byte("hello"))
	if b.Truncated() {
		t.Fatal("exact cap should not flag truncation")
	}
	if b.String() != "hello" {
		t.Fatalf("captured: %q", b.String())
	}
}
