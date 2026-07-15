import { useMemo, useState } from "react";
import type { ControllerDto } from "../lib/api";

interface ControllerChecklistProps {
  controllers: ControllerDto[];
  /** IDs de controlador atualmente marcados (inclui os travados). */
  selected: Set<string>;
  onChange: (next: Set<string>) => void;
  /** Portas herdadas do grupo: aparecem marcadas, travadas e com a tag "grupo". */
  lockedIds?: Set<string>;
  showIp?: boolean;
  emptyText?: string;
}

/**
 * Lista compacta de portas para marcar: busca, rolagem, "marcar/limpar todas" e itens travados
 * (portas herdadas do grupo). Encolhe o formulário em vez de espalhar dezenas de checkboxes.
 */
export function ControllerChecklist({
  controllers,
  selected,
  onChange,
  lockedIds,
  showIp = true,
  emptyText = "Nenhum controlador cadastrado ainda.",
}: ControllerChecklistProps) {
  const [query, setQuery] = useState("");
  const locked = lockedIds ?? new Set<string>();

  const visible = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return controllers;
    return controllers.filter((c) => c.name.toLowerCase().includes(q) || c.ipAddress.includes(q));
  }, [controllers, query]);

  const unlockedIds = controllers.filter((c) => !locked.has(c.id)).map((c) => c.id);
  const allSelected = unlockedIds.length > 0 && unlockedIds.every((id) => selected.has(id));
  const selectedCount = controllers.filter((c) => locked.has(c.id) || selected.has(c.id)).length;

  function toggle(id: string, checked: boolean) {
    if (locked.has(id)) return;
    const next = new Set(selected);
    if (checked) next.add(id);
    else next.delete(id);
    onChange(next);
  }

  function toggleAll() {
    const next = new Set(selected);
    if (allSelected) {
      for (const id of unlockedIds) next.delete(id);
    } else {
      for (const id of unlockedIds) next.add(id);
    }
    onChange(next);
  }

  if (controllers.length === 0) {
    return <p className="text-muted">{emptyText}</p>;
  }

  return (
    <div className="checklist">
      <div className="checklist-toolbar">
        <input
          type="search"
          placeholder="Filtrar portas..."
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          className="checklist-search"
        />
        <button type="button" className="btn btn-outline btn-sm" onClick={toggleAll}>
          {allSelected ? "Limpar" : "Marcar todas"}
        </button>
        <span className="text-muted checklist-count">
          {selectedCount} de {controllers.length}
        </span>
      </div>
      <div className="checklist-items">
        {visible.map((c) => {
          const isLocked = locked.has(c.id);
          return (
            <label key={c.id} className={`checklist-item${isLocked ? " is-locked" : ""}`}>
              <input
                type="checkbox"
                checked={isLocked || selected.has(c.id)}
                disabled={isLocked}
                onChange={(e) => toggle(c.id, e.target.checked)}
              />
              <span className="checklist-item-name">
                {c.name}
                {showIp && <span className="text-muted"> ({c.ipAddress})</span>}
              </span>
              {isLocked && <span className="tag">grupo</span>}
            </label>
          );
        })}
        {visible.length === 0 && <p className="text-muted checklist-empty">Nenhuma porta encontrada.</p>}
      </div>
    </div>
  );
}
