import { Component } from '@angular/core';
import { GridModule } from '@progress/kendo-angular-grid';
import { TooltipModule } from '@progress/kendo-angular-tooltip';

type Customer = {
  id: number;
  customerName: string;
  email: string;
  phone: string;
  status: string;
};

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [GridModule, TooltipModule],
  template: `
    <div
      kendoTooltip
      filter=".customer-tooltip-cell"
      [tooltipTemplate]="customerTooltipTemplate"
      position="right">

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

    </div>

    <ng-template #customerTooltipTemplate let-anchor>
      @if (getTooltipItem(anchor); as item) {
        <div style="padding: 8px;">
          <strong>{{ item.customerName }}</strong><br />
          Email: {{ item.email }}<br />
          Phone: {{ item.phone }}<br />
          Status: {{ item.status }}
        </div>
      }
    </ng-template>
  `
})
export class AppComponent {
  gridData: Customer[] = [
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

  getTooltipItem(anchor: HTMLElement): Customer | undefined {
    const id = Number(anchor.dataset['id']);
    return this.gridData.find(x => x.id === id);
  }
}
