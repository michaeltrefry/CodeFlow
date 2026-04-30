type MonacoEnvironmentLike = {
  getWorker?: (workerId: string, label: string) => Worker;
};

const editorWorkerFactory = () =>
  new Worker(new URL('./workers/editor.worker.ts', import.meta.url), {
    type: 'module',
    name: 'editorWorkerService'
  });

const typescriptWorkerFactory = () =>
  new Worker(new URL('./workers/ts.worker.ts', import.meta.url), {
    type: 'module',
    name: 'typescript'
  });

let configured = false;
let stylesLoaded: Promise<void> | undefined;

export function ensureMonacoEditorStyles(): Promise<void> {
  stylesLoaded ??= new Promise<void>((resolve, reject) => {
    if (typeof document === 'undefined') {
      resolve();
      return;
    }

    const existingLink = document.getElementById('cf-monaco-editor-styles') as HTMLLinkElement | null;
    if (existingLink) {
      resolve();
      return;
    }

    const link = document.createElement('link');
    link.id = 'cf-monaco-editor-styles';
    link.rel = 'stylesheet';
    link.href = new URL('assets/monaco-editor/vs/editor/editor.main.css', document.baseURI).toString();
    link.onload = () => resolve();
    link.onerror = () => {
      stylesLoaded = undefined;
      link.remove();
      reject(new Error('Failed to load Monaco editor styles.'));
    };
    document.head.appendChild(link);
  });
  return stylesLoaded;
}

export function ensureMonacoEnvironment(): void {
  if (configured) return;

  const scope = globalThis as typeof globalThis & {
    MonacoEnvironment?: MonacoEnvironmentLike;
  };

  scope.MonacoEnvironment = {
    ...scope.MonacoEnvironment,
    getWorker: (_workerId: string, label: string) => {
      switch (label) {
        case 'javascript':
        case 'typescript':
          return typescriptWorkerFactory();
        default:
          return editorWorkerFactory();
      }
    }
  };

  configured = true;
}
