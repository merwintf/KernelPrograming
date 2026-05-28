import { Component } from '@angular/core';

@Component({
  selector: 'app-root',
  template: `
    <kendo-tooltip
      filter=".customer-tooltip-cell"
      [tooltipTemplate]="customerTooltipTemplate"
      position="right">
    </kendo-tooltip>

    <kendo-grid [data]="gridData">
      <kendo-grid-column field="customerName" title="Customer">
        <ng-template kendoGridCellTemplate let-dataItem>
          <span
            class="customer-tooltip-cell"
            [attr.data-id]="dataItem.id">
            {{ dataItem.customerName }}
          </span>
        </ng-template>
      </kendo-grid-column>
    </kendo-grid>

    <ng-template #customerTooltipTemplate let-anchor>
      <ng-container *ngIf="getTooltipItem(anchor) as item">
        <div style="padding: 8px;">
          <strong>{{ item.customerName }}</strong><br />
          Email: {{ item.email }}<br />
          Phone: {{ item.phone }}<br />
          Status: {{ item.status }}
        </div>
      </ng-container>
    </ng-template>
  `
})
export class AppComponent {
  gridData = [
    {
      id: 1,
      customerName: 'John',
      email: 'john@test.com',
      phone: '12345',
      status: 'Active'
    },
    {
      id: 2,
      customerName: 'Mary',
      email: 'mary@test.com',
      phone: '67890',
      status: 'Inactive'
    }
  ];

  getTooltipItem(anchor: HTMLElement) {
    const id = Number(anchor.dataset['id']);
    return this.gridData.find(x => x.id === id);
  }
}
