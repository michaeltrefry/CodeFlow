import { CommonModule } from '@angular/common';
import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  EventEmitter,
  Input,
  NgZone,
  OnChanges,
  OnDestroy,
  Output,
  SimpleChanges,
  ViewChild,
  effect,
  inject
} from '@angular/core';
import { ThemeService } from '../../../core/theme.service';
import { ensureMonacoEnvironment } from './monaco-environment';

export interface MonacoMarker {
  startLineNumber: number;
  startColumn: number;
  endLineNumber: number;
  endColumn: number;
  message: string;
  severity?: 'error' | 'warning' | 'info';
}

/**
 * Monaco wrapper for editing scripts and templates anywhere in the authoring
 * surface. Monaco is lazy-loaded the first time the component mounts so route
 * bundles stay small; the fallback path (loader failure) still renders a
 * working textarea so authors are never locked out.
 */
@Component({
  selector: 'cf-monaco-script-editor',
  standalone: true,
  imports: [CommonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div #host class="monaco-host"></div>
    @if (fallback) {
      <textarea
        #fallbackArea
        class="monaco-fallback"
        [value]="value"
        (input)="onFallbackInput($event)"
        spellcheck="false"></textarea>
    }
  `,
  styles: [`
    :host {
      display: block;
      position: relative;
      min-height: 240px;
      border: 1px solid var(--border);
      border-radius: 4px;
      overflow: hidden;
    }
    .monaco-host { position: absolute; inset: 0; }
    .monaco-fallback {
      width: 100%;
      min-height: 240px;
      font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
      font-size: 13px;
      padding: 0.5rem;
      background: var(--bg);
      color: var(--text);
      border: 0;
    }
  `]
})
export class MonacoScriptEditorComponent implements AfterViewInit, OnChanges, OnDestroy {
  private readonly ngZone = inject(NgZone);
  private readonly themeService = inject(ThemeService);
  private readonly destroyRef = inject(DestroyRef);

  @ViewChild('host', { static: true }) hostRef!: ElementRef<HTMLDivElement>;

  @Input() value = '';
  @Input() language = 'javascript';
  @Input() markers: MonacoMarker[] = [];
  @Input() readOnly = false;

  @Output() valueChange = new EventEmitter<string>();

  fallback = false;

  private editor?: unknown;
  private monacoApi?: typeof import('monaco-editor');
  private model?: unknown;
  private disposeListeners: Array<() => void> = [];
  private readonly ownerId = `cf-monaco-${Math.random().toString(36).slice(2)}`;

  constructor() {
    effect(() => {
      const mode = this.themeService.theme();
      this.applyTheme(mode === 'light' ? 'vs' : 'vs-dark');
    });
  }

  async ngAfterViewInit(): Promise<void> {
    try {
      ensureMonacoEnvironment();
      const monaco = await import('monaco-editor');
      this.monacoApi = monaco;

      this.ngZone.runOutsideAngular(() => {
        const editor = monaco.editor.create(this.hostRef.nativeElement, {
          value: this.value,
          language: this.language,
          theme: this.themeService.theme() === 'light' ? 'vs' : 'vs-dark',
          minimap: { enabled: false },
          scrollBeyondLastLine: false,
          automaticLayout: true,
          fontSize: 13,
          tabSize: 2,
          wordWrap: 'on',
          fixedOverflowWidgets: true,
          readOnly: this.readOnly
        });

        const changeDisposable = editor.onDidChangeModelContent(() => {
          const current = editor.getValue();
          if (current !== this.value) {
            this.value = current;
            this.ngZone.run(() => this.valueChange.emit(current));
          }
        });
        this.disposeListeners.push(() => changeDisposable.dispose());

        this.editor = editor;
        this.model = editor.getModel();
        this.applyMarkers();
      });
    } catch (err) {
      console.warn('Monaco editor failed to load; falling back to plain textarea.', err);
      this.fallback = true;
    }
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (!this.editor || !this.monacoApi) return;

    if (changes['value']) {
      const editor = this.editor as {
        getValue(): string;
        setValue(value: string): void;
      };
      if (editor.getValue() !== this.value) {
        editor.setValue(this.value ?? '');
      }
    }

    if (changes['language'] && this.model) {
      this.monacoApi.editor.setModelLanguage(
        this.model as import('monaco-editor').editor.ITextModel,
        this.language
      );
    }

    if (changes['readOnly']) {
      const editor = this.editor as { updateOptions(opts: { readOnly: boolean }): void };
      editor.updateOptions({ readOnly: this.readOnly });
    }

    if (changes['markers']) {
      this.applyMarkers();
    }
  }

  ngOnDestroy(): void {
    for (const dispose of this.disposeListeners) {
      try { dispose(); } catch { /* ignore */ }
    }
    this.disposeListeners = [];
    const editor = this.editor as { dispose?: () => void } | undefined;
    editor?.dispose?.();
    const model = this.model as { dispose?: () => void } | undefined;
    model?.dispose?.();
  }

  onFallbackInput(event: Event): void {
    const value = (event.target as HTMLTextAreaElement).value;
    this.value = value;
    this.valueChange.emit(value);
  }

  private applyTheme(theme: 'vs' | 'vs-dark'): void {
    if (!this.monacoApi) return;
    this.monacoApi.editor.setTheme(theme);
  }

  private applyMarkers(): void {
    if (!this.monacoApi || !this.model) return;
    const severityLookup = {
      error: this.monacoApi.MarkerSeverity.Error,
      warning: this.monacoApi.MarkerSeverity.Warning,
      info: this.monacoApi.MarkerSeverity.Info
    } as const;
    const markers = this.markers.map(m => ({
      startLineNumber: m.startLineNumber,
      startColumn: m.startColumn,
      endLineNumber: m.endLineNumber,
      endColumn: m.endColumn,
      message: m.message,
      severity: severityLookup[m.severity ?? 'error']
    }));
    this.monacoApi.editor.setModelMarkers(this.model as import('monaco-editor').editor.ITextModel, this.ownerId, markers);
  }
}
