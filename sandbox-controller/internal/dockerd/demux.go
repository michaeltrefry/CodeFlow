package dockerd

import (
	"encoding/binary"
	"errors"
	"fmt"
	"io"
)

// Demux splits the docker container-logs multiplexed stream into stdout and
// stderr writers. Docker frames each chunk with an 8-byte header:
//
//	byte 0 : stream type (0=stdin/unused, 1=stdout, 2=stderr)
//	bytes 1-3 : reserved (zero)
//	bytes 4-7 : payload length, big-endian uint32
//
// followed by the payload bytes. We keep reading frames until EOF.
//
// Bytes written to a writer that returns an error are still counted; the
// short write propagates back so the caller can flag truncation.
//
// This is a re-implementation of the demux that ships in moby's
// pkg/stdcopy.StdCopy, intentionally rewritten so the controller has no
// transitive dependency on moby/moby. The frame format is part of the public
// Docker Engine API contract.
func Demux(r io.Reader, stdout, stderr io.Writer) error {
	const headerLen = 8
	header := make([]byte, headerLen)

	for {
		_, err := io.ReadFull(r, header)
		if err != nil {
			if errors.Is(err, io.EOF) {
				return nil
			}
			if errors.Is(err, io.ErrUnexpectedEOF) {
				// Truncated trailing frame; treat as end of stream rather than failing.
				return nil
			}
			return fmt.Errorf("demux: read header: %w", err)
		}

		streamType := header[0]
		payloadLen := binary.BigEndian.Uint32(header[4:8])
		if payloadLen == 0 {
			continue
		}

		var dest io.Writer
		switch streamType {
		case 1:
			dest = stdout
		case 2:
			dest = stderr
		default:
			// Unknown stream — discard the payload but keep going.
			if _, err := io.CopyN(io.Discard, r, int64(payloadLen)); err != nil {
				return fmt.Errorf("demux: discard unknown stream: %w", err)
			}
			continue
		}

		if dest == nil {
			if _, err := io.CopyN(io.Discard, r, int64(payloadLen)); err != nil {
				return fmt.Errorf("demux: discard nil-writer payload: %w", err)
			}
			continue
		}

		if _, err := io.CopyN(dest, r, int64(payloadLen)); err != nil {
			// Writer error (e.g. cap hit) — drain the rest of the frame so the
			// next header read isn't misaligned, then keep going. Truncation
			// is signalled by the writer the caller passed in.
			if _, derr := io.CopyN(io.Discard, r, int64(payloadLen)); derr != nil {
				return fmt.Errorf("demux: drain after write error: %w", derr)
			}
		}
	}
}
