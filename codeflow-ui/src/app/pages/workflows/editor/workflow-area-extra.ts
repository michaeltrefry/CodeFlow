import { AngularArea2D } from 'rete-angular-plugin/20';
import type { ClassicPreset, GetSchemes } from 'rete';

type ClassicSchemes = GetSchemes<
  ClassicPreset.Node,
  ClassicPreset.Connection<ClassicPreset.Node, ClassicPreset.Node>
>;

export type AreaExtra<Schemes extends ClassicSchemes = ClassicSchemes> = AngularArea2D<Schemes>;
