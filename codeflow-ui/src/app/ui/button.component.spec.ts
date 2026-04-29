import { TestBed } from '@angular/core/testing';
import { ButtonComponent } from './button.component';

describe('ButtonComponent', () => {
  it('reflects variant, boolean, icon, and type inputs on the host button', async () => {
    await TestBed.configureTestingModule({
      imports: [ButtonComponent],
    }).compileComponents();

    const fixture = TestBed.createComponent(ButtonComponent);
    fixture.componentRef.setInput('variant', 'danger');
    fixture.componentRef.setInput('size', 'sm');
    fixture.componentRef.setInput('icon', 'trash');
    fixture.componentRef.setInput('iconOnly', '');
    fixture.componentRef.setInput('active', true);
    fixture.componentRef.setInput('disabled', 'true');
    fixture.componentRef.setInput('type', 'submit');
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;

    expect(host.getAttribute('type')).toBe('submit');
    expect(host.classList.contains('btn')).toBe(true);
    expect(host.classList.contains('btn-danger')).toBe(true);
    expect(host.classList.contains('btn-sm')).toBe(true);
    expect(host.classList.contains('btn-icon')).toBe(true);
    expect(host.getAttribute('data-active')).toBe('true');
    expect(host.hasAttribute('disabled')).toBe(true);
    expect(host.querySelector('cf-icon')).not.toBeNull();
  });
});
