import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ChatComposerComponent } from './chat-composer.component';

describe('ChatComposerComponent', () => {
  let fixture: ComponentFixture<ChatComposerComponent>;
  let component: ChatComposerComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ChatComposerComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(ChatComposerComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('emits trimmed text and clears the textarea on form submit', () => {
    const sent: string[] = [];
    component.send.subscribe(value => sent.push(value));
    const textarea = fixture.nativeElement.querySelector('textarea') as HTMLTextAreaElement;
    const form = fixture.nativeElement.querySelector('form') as HTMLFormElement;

    textarea.value = '  summarize this trace  ';
    textarea.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    form.dispatchEvent(new Event('submit'));
    fixture.detectChanges();

    expect(sent).toEqual(['summarize this trace']);
    expect(textarea.value).toBe('');
  });

  it('sends on Enter while preserving Shift+Enter for multiline input', () => {
    const sent: string[] = [];
    component.send.subscribe(value => sent.push(value));
    const textarea = fixture.nativeElement.querySelector('textarea') as HTMLTextAreaElement;

    textarea.value = 'Run the workflow';
    textarea.dispatchEvent(new Event('input'));

    const multiline = new KeyboardEvent('keydown', { key: 'Enter', shiftKey: true });
    const multilinePrevent = vi.spyOn(multiline, 'preventDefault');
    textarea.dispatchEvent(multiline);
    expect(multilinePrevent).not.toHaveBeenCalled();
    expect(sent).toEqual([]);

    const submit = new KeyboardEvent('keydown', { key: 'Enter' });
    const submitPrevent = vi.spyOn(submit, 'preventDefault');
    textarea.dispatchEvent(submit);

    expect(submitPrevent).toHaveBeenCalled();
    expect(sent).toEqual(['Run the workflow']);
  });

  it('sends on a Send button click without relying on form submission', () => {
    // Regression: <cf-button type="submit"> is a custom element, so the browser does NOT trigger
    // form submission when it's clicked — only the explicit (click) handler does. Without the
    // (click) wiring on the Send button the only working send path was the textarea's Enter key.
    const sent: string[] = [];
    component.send.subscribe(value => sent.push(value));

    const textarea = fixture.nativeElement.querySelector('textarea') as HTMLTextAreaElement;
    textarea.value = 'click-fire';
    textarea.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    const buttons = Array.from(fixture.nativeElement.querySelectorAll('cf-button')) as HTMLElement[];
    const submitButton = buttons.find(button => button.textContent?.trim() === 'Send');
    expect(submitButton).toBeTruthy();
    submitButton!.click();
    fixture.detectChanges();

    expect(sent).toEqual(['click-fire']);
    expect(textarea.value).toBe('');
  });

  it('blocks blank or busy sends and exposes cancel while busy', () => {
    const sent: string[] = [];
    const cancelled: void[] = [];
    component.send.subscribe(value => sent.push(value));
    component.cancel.subscribe(() => cancelled.push(undefined));
    fixture.componentRef.setInput('busy', true);
    fixture.detectChanges();

    const buttons = Array.from(fixture.nativeElement.querySelectorAll('cf-button')) as HTMLElement[];
    const cancelButton = buttons.find(button => button.textContent?.includes('Cancel'));
    const submitButton = buttons.find(button => button.textContent?.includes('Streaming'));

    expect(cancelButton).toBeTruthy();
    expect(submitButton?.hasAttribute('disabled')).toBe(true);
    cancelButton?.click();
    fixture.detectChanges();

    expect(cancelled).toHaveLength(1);
    expect(sent).toEqual([]);
  });
});
