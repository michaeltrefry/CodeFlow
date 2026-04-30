import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ChatMessageComponent } from './chat-message.component';

describe('ChatMessageComponent', () => {
  let fixture: ComponentFixture<ChatMessageComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ChatMessageComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(ChatMessageComponent);
  });

  it('renders user messages as plain text so HTML is not interpreted', () => {
    fixture.componentRef.setInput('message', {
      id: 'message-1',
      role: 'user',
      content: '<strong>do not render</strong>',
    });
    fixture.detectChanges();

    const body = fixture.nativeElement.querySelector('.chat-msg-body') as HTMLElement;
    expect(fixture.nativeElement.querySelector('.chat-msg-role')?.textContent).toContain('You');
    expect(body.textContent).toContain('<strong>do not render</strong>');
    expect(body.querySelector('strong')).toBeNull();
  });

  it('renders assistant markdown and provider metadata after streaming completes', () => {
    fixture.componentRef.setInput('message', {
      id: 'message-2',
      role: 'assistant',
      content: 'Use **workflow vars**',
      provider: 'openai',
      model: 'gpt-test',
    });
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.chat-msg-role')?.textContent).toContain('Assistant');
    expect(fixture.nativeElement.querySelector('.chat-msg-body strong')?.textContent).toBe('workflow vars');
    expect(fixture.nativeElement.querySelector('.chat-msg-meta')?.textContent).toContain('openai');
    expect(fixture.nativeElement.querySelector('.chat-msg-meta')?.textContent).toContain('gpt-test');
  });

  it('shows streaming state and suppresses finalized provider metadata while pending', () => {
    fixture.componentRef.setInput('message', {
      id: null,
      role: 'assistant',
      content: 'Partial',
      provider: 'anthropic',
      model: 'claude-test',
      pending: true,
    });
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.chat-msg-pending')?.textContent).toContain('streaming');
    expect(fixture.nativeElement.querySelector('.chat-msg-spinner')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('.chat-msg-meta')).toBeNull();
  });
});
