import type { ServiceBlueprint } from '../types.js';
import { hydrateServiceBlueprintDefinition } from '../blueprint-hydration.js';

export function cloneAuthoredServiceBlueprint(serviceBlueprint: ServiceBlueprint): ServiceBlueprint {
  return hydrateServiceBlueprintDefinition(JSON.parse(JSON.stringify(serviceBlueprint)) as ServiceBlueprint);
}

export const PLANNING_SERVICE_BLUEPRINT: ServiceBlueprint = hydrateServiceBlueprintDefinition({
  "definitionKey": "planning-application",
  "displayName": "Planning Application",
  "version": 1,
  "initialStage": "declaration",
  "requestPolicy": "single",
  "description": "Standard planning application serviceBlueprint for submitting and tracking planning permission requests.",
  "schemaVersion": "1.0",
  "queues": [
    {
      "actor": "applicant",
      "displayName": "Applicant",
      "key": "applicant",
      "roleGates": [],
      "tags": {}
    }
  ],
  "stages": [
    {
      "actions": [
        {
          "type": "forms.load",
          "parameterSchemaKey": "forms-form-definition",
          "params": {
            "formDefinitionId": "planning-declaration"
          },
          "summary": "Load the declaration form.",
          "timing": "onEnter"
        }
      ],
      "actor": "applicant",
      "components": [
        {
          "type": "fieldset",
          "children": [
            {
              "type": "text",
              "fieldKey": "applicant-name",
              "hint": "Enter the full name of the person or organisation applying.",
              "label": "Applicant name",
              "required": true
            },
            {
              "type": "textarea",
              "fieldKey": "site-address",
              "hint": "Enter the full address of the site where development is proposed.",
              "label": "Site address",
              "required": true
            }
          ],
          "legend": "Declaration",
          "legendSize": "m"
        }
      ],
      "description": "Collects applicant and site identity before the full planning form.",
      "displayName": "Declaration",
      "queueKey": "applicant",
      "roleGates": [],
      "routes": [
        {
          "actions": [],
          "id": "declaration--route--route-application-form",
          "target": "route-application-form",
          "trigger": "route"
        }
      ],
      "stageKey": "declaration",
      "stageType": "Question"
    },
    {
      "actions": [
        {
          "type": "forms.save",
          "parameterSchemaKey": "forms-form-definition",
          "params": {
            "formDefinitionId": "planning-application"
          },
          "summary": "Persist the application form before moving on.",
          "timing": "onExit"
        }
      ],
      "actor": "applicant",
      "components": [
        {
          "type": "fieldset",
          "children": [
            {
              "type": "textarea",
              "fieldKey": "description",
              "hint": "Provide a clear description of the development you are proposing.",
              "label": "Description of proposed works",
              "required": true
            },
            {
              "type": "select",
              "fieldKey": "development-type",
              "label": "Type of development",
              "options": [
                "New build",
                "Extension",
                "Change of use",
                "Demolition",
                "Other"
              ],
              "required": true
            }
          ],
          "legend": "Application Form",
          "legendSize": "m"
        }
      ],
      "description": "Captures the substantive planning request.",
      "displayName": "Application Form",
      "queueKey": "applicant",
      "roleGates": [],
      "routes": [
        {
          "actions": [],
          "id": "application-form--route--route-check-answers",
          "target": "route-check-answers",
          "trigger": "route"
        }
      ],
      "stageKey": "application-form",
      "stageType": "Question"
    },
    {
      "actions": [],
      "actor": "applicant",
      "components": [],
      "description": "Summarises captured answers before final submission.",
      "displayName": "Check your answers",
      "queueKey": "applicant",
      "roleGates": [],
      "routes": [
        {
          "actions": [],
          "id": "check-answers--route--route-submitted",
          "target": "route-submitted",
          "trigger": "route"
        }
      ],
      "stageKey": "check-answers",
      "stageType": "CheckAnswers"
    },
    {
      "actions": [],
      "actor": "applicant",
      "components": [],
      "description": "Confirms receipt and moves the case into reviewer handling.",
      "displayName": "Application submitted",
      "queueKey": "applicant",
      "roleGates": [],
      "routes": [],
      "stageKey": "submitted",
      "stageType": "Confirmation"
    }
  ],
  "gateways": [
    {
      "actor": "applicant",
      "displayName": "Route to application form",
      "gatewayType": "Split",
      "key": "route-application-form",
      "queueKey": "applicant",
      "requiredIncomingQueues": [],
      "roleGates": [],
      "routes": [
        {
          "actions": [],
          "id": "route-application-form--continue--application-form",
          "target": "application-form",
          "trigger": "continue"
        }
      ]
    },
    {
      "actor": "applicant",
      "displayName": "Route to check answers",
      "gatewayType": "Split",
      "key": "route-check-answers",
      "queueKey": "applicant",
      "requiredIncomingQueues": [],
      "roleGates": [],
      "routes": [
        {
          "actions": [],
          "id": "route-check-answers--continue--check-answers",
          "target": "check-answers",
          "trigger": "continue"
        }
      ]
    },
    {
      "actor": "applicant",
      "displayName": "Route to submitted",
      "gatewayType": "Split",
      "key": "route-submitted",
      "queueKey": "applicant",
      "requiredIncomingQueues": [],
      "roleGates": [],
      "routes": [
        {
          "actions": [
            {
              "type": "forms.submit",
              "parameterSchemaKey": "forms-form-definition",
              "params": {
                "formDefinitionId": "planning-application"
              },
              "summary": "Submit the application form to the business app.",
              "timing": "onTransition"
            }
          ],
          "id": "route-submitted--submit--submitted",
          "showWhen": "application.isComplete == true",
          "target": "submitted",
          "trigger": "submit"
        }
      ]
    }
  ]
});

export const LEAVE_REQUEST_STARTER_SERVICE_BLUEPRINT: ServiceBlueprint = hydrateServiceBlueprintDefinition({
  "definitionKey": "leave-request",
  "displayName": "Leave Request",
  "version": 1,
  "initialStage": "start-request",
  "requestPolicy": "multiple",
  "schemaVersion": "1.0",
  "queues": [
    {
      "actor": "applicant",
      "displayName": "Applicant",
      "key": "applicant",
      "roleGates": [],
      "tags": {}
    },
    {
      "actor": "reviewer",
      "displayName": "Reviewer",
      "key": "reviewer",
      "roleGates": [
        "reviewer"
      ],
      "tags": {}
    }
  ],
  "stages": [
    {
      "actions": [],
      "actor": "applicant",
      "components": [],
      "description": "Collect the request details before the service branches into review work.",
      "displayName": "Start request",
      "queueKey": "applicant",
      "roleGates": [],
      "routes": [
        {
          "actions": [],
          "id": "start-request--route--review-split",
          "target": "review-split",
          "trigger": "route"
        }
      ],
      "stageKey": "start-request",
      "stageType": "Question"
    },
    {
      "actions": [],
      "actor": "applicant",
      "components": [],
      "description": "Applicant updates the request when more detail is needed.",
      "displayName": "Applicant amendments",
      "queueKey": "applicant",
      "roleGates": [],
      "routes": [
        {
          "actions": [],
          "id": "applicant-amendments--finish amendments--decision-join",
          "target": "decision-join",
          "trigger": "finish amendments"
        }
      ],
      "stageKey": "applicant-amendments",
      "stageType": "Question"
    },
    {
      "actions": [],
      "actor": "applicant",
      "components": [],
      "description": "Applicant provides the supporting documents for the request.",
      "displayName": "Upload evidence",
      "queueKey": "applicant",
      "roleGates": [],
      "routes": [
        {
          "actions": [],
          "id": "upload-evidence--evidence complete--decision-join",
          "target": "decision-join",
          "trigger": "evidence complete"
        }
      ],
      "stageKey": "upload-evidence",
      "stageType": "Question"
    },
    {
      "actions": [],
      "actor": "reviewer",
      "components": [],
      "description": "Reviewer checks the request before the service can continue.",
      "displayName": "Reviewer assessment",
      "queueKey": "reviewer",
      "roleGates": [
        "reviewer"
      ],
      "routes": [
        {
          "actions": [],
          "id": "reviewer-assessment--confirm review--decision-join",
          "requiresRole": "reviewer",
          "target": "decision-join",
          "trigger": "confirm review"
        }
      ],
      "stageKey": "reviewer-assessment",
      "stageType": "Question"
    },
    {
      "actions": [],
      "actor": "applicant",
      "components": [],
      "description": "The shared path continues here once every branch is complete.",
      "displayName": "Decision confirmed",
      "queueKey": "applicant",
      "roleGates": [],
      "routes": [],
      "stageKey": "decision-confirmed",
      "stageType": "Confirmation"
    }
  ],
  "gateways": [
    {
      "actor": "applicant",
      "description": "Branch the request into the next pieces of work.",
      "displayName": "Review split",
      "gatewayType": "Split",
      "key": "review-split",
      "queueKey": "applicant",
      "requiredIncomingQueues": [],
      "roleGates": [],
      "routes": [
        {
          "actions": [],
          "id": "review-split--request amendments--applicant-amendments",
          "target": "applicant-amendments",
          "trigger": "request amendments"
        },
        {
          "actions": [],
          "id": "review-split--upload evidence--upload-evidence",
          "target": "upload-evidence",
          "trigger": "upload evidence"
        },
        {
          "actions": [],
          "id": "review-split--send to reviewer--reviewer-assessment",
          "requiresRole": "reviewer",
          "target": "reviewer-assessment",
          "trigger": "send to reviewer"
        }
      ]
    },
    {
      "actor": "applicant",
      "description": "Wait for every branch to complete before releasing the next step.",
      "displayName": "Decision join",
      "gatewayType": "Join",
      "key": "decision-join",
      "queueKey": "applicant",
      "requiredIncomingQueues": [
        "applicant",
        "reviewer"
      ],
      "roleGates": [],
      "routes": [
        {
          "actions": [],
          "id": "decision-join--continue--decision-confirmed",
          "target": "decision-confirmed",
          "trigger": "continue"
        }
      ],
      "waitingAllowDefer": false,
      "waitingContent": "Waiting for amendments, supporting evidence, and reviewer assessment before the decision can continue."
    }
  ]
});

export const PAYMENT_DEMO_SERVICE_BLUEPRINT: ServiceBlueprint = hydrateServiceBlueprintDefinition({
  "definitionKey": "payment-demo",
  "displayName": "Payment Demo",
  "version": 1,
  "initialStage": "enter-details",
  "requestPolicy": "single",
  "description": "Payment flow showing the web queue handing off to the business queue before completion.",
  "schemaVersion": "1.0",
  "queues": [
    {
      "actor": "applicant",
      "displayName": "Applicant",
      "key": "web-user",
      "roleGates": [],
      "tags": {}
    },
    {
      "actor": "reviewer",
      "displayName": "Payments team",
      "key": "business-user",
      "roleGates": [
        "reviewer"
      ],
      "tags": {}
    }
  ],
  "stages": [
    {
      "actions": [],
      "actor": "applicant",
      "components": [
        {
          "type": "fieldset",
          "children": [
            {
              "type": "text",
              "fieldKey": "cardholderName",
              "label": "Cardholder name",
              "required": true
            },
            {
              "type": "decimal",
              "fieldKey": "amount",
              "label": "Amount (£)",
              "required": true
            }
          ],
          "legend": "Enter Payment Details"
        }
      ],
      "displayName": "Enter payment details",
      "queueKey": "web-user",
      "roleGates": [],
      "routes": [
        {
          "actions": [],
          "id": "enter-details--submit--submit-payment",
          "target": "submit-payment",
          "trigger": "submit"
        }
      ],
      "stageKey": "enter-details",
      "stageType": "Question"
    },
    {
      "actions": [],
      "actor": "reviewer",
      "components": [],
      "description": "Back-office confirmation step for reconciling the payment before the applicant is released.",
      "displayName": "Confirm payment received",
      "queueKey": "business-user",
      "roleGates": [
        "reviewer"
      ],
      "routes": [
        {
          "actions": [],
          "id": "confirm-payment-received--confirm--await-payment-confirmation",
          "requiresRole": "reviewer",
          "target": "await-payment-confirmation",
          "trigger": "confirm"
        }
      ],
      "stageKey": "confirm-payment-received",
      "stageType": "Question"
    },
    {
      "actions": [],
      "actor": "applicant",
      "components": [],
      "description": "Payment received. A receipt has been sent to your email address.",
      "displayName": "Payment complete",
      "queueKey": "web-user",
      "roleGates": [],
      "routes": [],
      "stageKey": "payment-complete",
      "stageType": "Confirmation"
    }
  ],
  "gateways": [
    {
      "actor": "applicant",
      "displayName": "Submit payment → notify back-office",
      "gatewayType": "Split",
      "key": "submit-payment",
      "queueKey": "web-user",
      "requiredIncomingQueues": [],
      "roleGates": [],
      "routes": [
        {
          "actions": [],
          "id": "submit-payment--submit--await-payment-confirmation",
          "target": "await-payment-confirmation",
          "trigger": "submit"
        },
        {
          "actions": [],
          "id": "submit-payment--submit--confirm-payment-received",
          "target": "confirm-payment-received",
          "trigger": "submit"
        }
      ]
    },
    {
      "actor": "applicant",
      "displayName": "Awaiting payment confirmation",
      "gatewayType": "Join",
      "key": "await-payment-confirmation",
      "queueKey": "web-user",
      "requiredIncomingQueues": [
        "web-user",
        "business-user"
      ],
      "roleGates": [],
      "routes": [
        {
          "actions": [],
          "id": "await-payment-confirmation--release--payment-complete",
          "target": "payment-complete",
          "trigger": "release"
        }
      ],
      "waitingAllowDefer": true,
      "waitingContent": "We are waiting for the payments team to confirm receipt of your payment.",
      "waitingDeferMessage": "You can leave this page and return later. We will update this payment as soon as the confirmation arrives.",
      "waitingExpectedSeconds": 60,
      "waitingPollIntervalMs": 5000
    }
  ]
});

/**
 * Community Enquiry serviceBlueprint — migrated to queues/gateways/routes format.
 * Single-queue (applicant), simple linear flow with one Split gateway.
 */
export const COMMUNITY_ENQUIRY_SERVICE_BLUEPRINT: ServiceBlueprint = hydrateServiceBlueprintDefinition({
  "definitionKey": "community-enquiry",
  "displayName": "Get in Touch",
  "version": 1,
  "initialStage": "collecting-details",
  "requestPolicy": "single",
  "description": "Simple contact serviceBlueprint for community enquiries.",
  "schemaVersion": "1.0",
  "queues": [
    {
      "actor": "applicant",
      "displayName": "Applicant",
      "key": "applicant",
      "roleGates": [],
      "tags": {}
    }
  ],
  "stages": [
    {
      "actions": [],
      "components": [],
      "displayName": "Your details",
      "queueKey": "applicant",
      "roleGates": [],
      "routes": [
        {
          "id": "collecting-details--submit--route-submitted",
          "target": "route-submitted",
          "trigger": "submit"
        }
      ],
      "stageKey": "collecting-details",
      "stageType": "Question"
    },
    {
      "actions": [],
      "components": [],
      "displayName": "Thank you",
      "queueKey": "applicant",
      "roleGates": [],
      "routes": [],
      "stageKey": "submitted",
      "stageType": "Confirmation"
    }
  ],
  "gateways": [
    {
      "displayName": "Route to submitted",
      "gatewayType": "Split",
      "key": "route-submitted",
      "queueKey": "applicant",
      "requiredIncomingQueues": [],
      "roleGates": [],
      "routes": [
        {
          "actions": [],
          "id": "collecting-details--submit--submitted",
          "target": "submitted",
          "trigger": "submit"
        }
      ]
    }
  ]
});

/**
 * Information Request serviceBlueprint — migrated to queues/gateways/routes format.
 * Two-queue (applicant + caseworker) with a Split gateway and a Join gateway.
 */
export const INFORMATION_REQUEST_SERVICE_BLUEPRINT: ServiceBlueprint = hydrateServiceBlueprintDefinition({
  "definitionKey": "information-request",
  "displayName": "Information Request",
  "version": 1,
  "initialStage": "collecting-info",
  "requestPolicy": "single",
  "schemaVersion": "1.0",
  "queues": [
    {
      "actor": "applicant",
      "displayName": "Applicant",
      "key": "applicant",
      "roleGates": [],
      "tags": {}
    },
    {
      "actor": "caseworker",
      "displayName": "Caseworker",
      "key": "caseworker",
      "roleGates": [],
      "tags": {}
    }
  ],
  "stages": [
    {
      "actions": [],
      "components": [],
      "displayName": "Tell us about yourself",
      "queueKey": "applicant",
      "roleGates": [],
      "routes": [
        {
          "id": "collecting-info--submit--request-submitted",
          "target": "request-submitted",
          "trigger": "submit"
        }
      ],
      "stageKey": "collecting-info",
      "stageType": "Question"
    },
    {
      "actions": [],
      "components": [],
      "description": "Caseworker confirms the review outcome before the applicant sees the final status.",
      "displayName": "Caseworker review",
      "queueKey": "caseworker",
      "roleGates": [],
      "routes": [
        {
          "id": "caseworker-review--complete-review--caseworker-route",
          "target": "caseworker-route",
          "trigger": "complete-review"
        }
      ],
      "stageKey": "caseworker-review",
      "stageType": "Question"
    },
    {
      "actions": [],
      "components": [],
      "displayName": "Request Complete",
      "queueKey": "applicant",
      "roleGates": [],
      "routes": [],
      "stageKey": "complete",
      "stageType": "Confirmation"
    }
  ],
  "gateways": [
    {
      "displayName": "Request submitted",
      "gatewayType": "Split",
      "key": "request-submitted",
      "queueKey": "applicant",
      "requiredIncomingQueues": [],
      "roleGates": [],
      "routes": [
        {
          "actions": [],
          "id": "collecting-info--submit--review-complete",
          "target": "review-complete",
          "trigger": "submit"
        },
        {
          "actions": [],
          "id": "collecting-info--submit--caseworker-review",
          "target": "caseworker-review",
          "trigger": "submit"
        }
      ]
    },
    {
      "displayName": "Route from caseworker review",
      "gatewayType": "Split",
      "key": "caseworker-route",
      "queueKey": "caseworker",
      "requiredIncomingQueues": [],
      "roleGates": [],
      "routes": [
        {
          "actions": [],
          "id": "caseworker-review--complete-review--review-complete",
          "target": "review-complete",
          "trigger": "complete-review"
        }
      ]
    },
    {
      "displayName": "Review complete",
      "gatewayType": "Join",
      "key": "review-complete",
      "queueKey": "applicant",
      "requiredIncomingQueues": [
        "applicant",
        "caseworker"
      ],
      "roleGates": [],
      "routes": [
        {
          "actions": [],
          "id": "review-complete--release--complete",
          "target": "complete",
          "trigger": "release"
        }
      ],
      "waitingAllowDefer": false,
      "waitingContent": "We've received your submission and it's currently being reviewed.",
      "waitingExpectedSeconds": 30,
      "waitingPollIntervalMs": 5000
    }
  ]
});

/**
 * Planning Application serviceBlueprint — migrated to queues/gateways/routes format.
 * Single-queue (applicant), linear flow through declaration → form → check → submitted.
 */
export const PLANNING_SERVICE_BLUEPRINT_MIGRATED: ServiceBlueprint = hydrateServiceBlueprintDefinition({
  "definitionKey": "planning-application",
  "displayName": "Planning Application",
  "version": 1,
  "initialStage": "declaration",
  "requestPolicy": "single",
  "description": "Standard planning application serviceBlueprint for submitting and tracking planning permission requests.",
  "schemaVersion": "1.0",
  "queues": [
    {
      "actor": "applicant",
      "displayName": "Applicant",
      "key": "applicant",
      "roleGates": [],
      "tags": {}
    }
  ],
  "stages": [
    {
      "actions": [],
      "actor": "applicant",
      "components": [],
      "description": "Collects applicant and site identity before the full planning form.",
      "displayName": "Declaration",
      "queueKey": "applicant",
      "roleGates": [],
      "routes": [
        {
          "id": "declaration--continue--route-application-form",
          "target": "route-application-form",
          "trigger": "continue"
        }
      ],
      "stageKey": "declaration",
      "stageType": "Question"
    },
    {
      "actions": [],
      "actor": "applicant",
      "components": [],
      "description": "Captures the substantive planning request.",
      "displayName": "Application Form",
      "queueKey": "applicant",
      "roleGates": [],
      "routes": [
        {
          "id": "application-form--continue--route-check-answers",
          "target": "route-check-answers",
          "trigger": "continue"
        }
      ],
      "stageKey": "application-form",
      "stageType": "Question"
    },
    {
      "actions": [],
      "actor": "applicant",
      "components": [],
      "description": "Summarises captured answers before final submission.",
      "displayName": "Check your answers",
      "queueKey": "applicant",
      "roleGates": [],
      "routes": [
        {
          "id": "check-answers--submit--route-submitted",
          "target": "route-submitted",
          "trigger": "submit"
        }
      ],
      "stageKey": "check-answers",
      "stageType": "CheckAnswers"
    },
    {
      "actions": [],
      "actor": "applicant",
      "components": [],
      "description": "Confirms receipt and moves the case into reviewer handling.",
      "displayName": "Application submitted",
      "queueKey": "applicant",
      "roleGates": [],
      "routes": [],
      "stageKey": "submitted",
      "stageType": "Confirmation"
    }
  ],
  "gateways": [
    {
      "displayName": "Route to application form",
      "gatewayType": "Split",
      "key": "route-application-form",
      "queueKey": "applicant",
      "requiredIncomingQueues": [],
      "roleGates": [],
      "routes": [
        {
          "actions": [],
          "id": "declaration--continue--application-form",
          "target": "application-form",
          "trigger": "continue"
        }
      ]
    },
    {
      "displayName": "Route to check answers",
      "gatewayType": "Split",
      "key": "route-check-answers",
      "queueKey": "applicant",
      "requiredIncomingQueues": [],
      "roleGates": [],
      "routes": [
        {
          "actions": [],
          "id": "application-form--continue--check-answers",
          "target": "check-answers",
          "trigger": "continue"
        }
      ]
    },
    {
      "displayName": "Route to submitted",
      "gatewayType": "Split",
      "key": "route-submitted",
      "queueKey": "applicant",
      "requiredIncomingQueues": [],
      "roleGates": [],
      "routes": [
        {
          "actions": [],
          "id": "check-answers--submit--submitted",
          "target": "submitted",
          "trigger": "submit"
        }
      ]
    }
  ]
});

/**
 * Money Modeller — the fully declarative pension modeller demo (see
 * UmbracoPrism.MockBusinessApp/serviceBlueprint-seeds/money-modeller.json, mirrored
 * here rather than imported so the fixture stays a plain TS literal like the
 * rest of this file). Two queues, a calculations block driving live
 * stat-group/chart components, a recalculate self-loop, and a fan-out to a
 * back-office review queue — the most structurally complex real serviceBlueprint
 * this repo ships, and the one that originally surfaced the graph canvas's
 * chip-collision and edge-routing issues.
 */
export const MONEY_MODELLER_SERVICE_BLUEPRINT: ServiceBlueprint = hydrateServiceBlueprintDefinition({
  "definitionKey": "money-modeller",
  "displayName": "Money Modeller",
  "version": 1,
  "initialStage": "choose-start",
  "requestPolicy": "single",
  "description": "Interactive pension benefit modeller: model retirement scenarios from your record or a formal quote, then hand a chosen scenario to the scheme administrators as a quote request.",
  "schemaVersion": "1.0",
  "calculations": {
    "tables": {
      "pensionAgeFactor": {
        "interpolate": "linear",
        "values": {
          "55": 0.56,
          "66": 1,
          "75": 1.27
        }
      },
      "lumpAgeFactor": {
        "interpolate": "linear",
        "values": {
          "55": 0.725,
          "66": 1,
          "75": 1
        }
      }
    },
    "fields": {
      "member": {
        "source": "service"
      },
      "quoteMode": {
        "expr": "qPension > 0"
      },
      "todaysMoney": {
        "expr": "moneyBasis <> 'Future money'"
      },
      "npa": {
        "expr": "66"
      },
      "statePensionAge": {
        "expr": "68"
      },
      "minRetireAge": {
        "expr": "max(55, member.age + 1)"
      },
      "maxRetireAge": {
        "expr": "75"
      },
      "retireAgeEff": {
        "expr": "clamp(if(quoteMode, qAge, retireAge), minRetireAge, maxRetireAge)"
      },
      "hasDc": {
        "expr": "if(quoteMode, qDC > 0, member.dcPot > 0 or (member.active and member.salary > 74208))"
      },
      "years": {
        "expr": "max(0, retireAgeEff - member.age)"
      },
      "realGrowth": {
        "expr": "(salaryGrowth - inflation) / 100"
      },
      "realReturn": {
        "expr": "(invReturn - inflation) / 100"
      },
      "cappedSalary": {
        "expr": "min(member.salary, 74208)"
      },
      "futurePension": {
        "expr": "if(member.active and not quoteMode, years * (cappedSalary / 75) * pow(1 + max(realGrowth, -0.05), years / 2), 0)"
      },
      "basePension": {
        "expr": "if(quoteMode, qPension, member.accruedPension + futurePension)"
      },
      "baseLump": {
        "expr": "if(quoteMode, qLump, member.accruedLump + 3 * futurePension)"
      },
      "annualDc": {
        "expr": "if(member.active and not quoteMode, max(0, member.salary - 74208) * 0.2, 0)"
      },
      "growthFactor": {
        "expr": "pow(1 + realReturn, years)"
      },
      "newDcSavings": {
        "expr": "if(abs(realReturn) > 0.0001, annualDc * ((growthFactor - 1) / realReturn), annualDc * years)"
      },
      "pot": {
        "expr": "if(quoteMode, qDC, member.dcPot * growthFactor + newDcSavings)"
      },
      "pensionFactor": {
        "expr": "if(quoteMode, 1, lookup(pensionAgeFactor, retireAgeEff))"
      },
      "lumpFactor": {
        "expr": "if(quoteMode, 1, lookup(lumpAgeFactor, retireAgeEff))"
      },
      "moneyFactor": {
        "expr": "if(todaysMoney, 1, pow(1 + inflation / 100, years))"
      },
      "adjPension": {
        "expr": "basePension * pensionFactor * moneyFactor"
      },
      "adjLump": {
        "expr": "baseLump * lumpFactor * moneyFactor"
      },
      "adjPot": {
        "expr": "pot * moneyFactor"
      },
      "totalValue": {
        "expr": "20 * adjPension + adjLump + adjPot"
      },
      "maxTfc": {
        "expr": "0.25 * totalValue"
      },
      "extraTfc": {
        "expr": "max(0, maxTfc - adjLump)"
      },
      "tfcFromDc": {
        "expr": "min(adjPot, extraTfc)"
      },
      "tfcShortfall": {
        "expr": "extraTfc - tfcFromDc"
      },
      "pensionOut": {
        "expr": "if(benefitOption = 'Maximum tax-free cash', max(0, adjPension - tfcShortfall / 12), adjPension)"
      },
      "cashOut": {
        "expr": "if(benefitOption = 'Maximum tax-free cash', maxTfc, if(benefitOption = 'Take DC pot as cash', adjLump + adjPot, adjLump))"
      },
      "potOut": {
        "expr": "if(benefitOption = 'Maximum tax-free cash', adjPot - tfcFromDc, if(benefitOption = 'Take DC pot as cash', 0, adjPot))"
      },
      "dcIncomeOut": {
        "expr": "potOut / 20"
      },
      "statePension": {
        "expr": "11975 * moneyFactor"
      },
      "cashLabel": {
        "expr": "if(benefitOption = 'Take DC pot as cash', 'One-off cash', 'Tax-free cash')"
      },
      "resultPension": {
        "expr": "round(pensionOut)",
        "format": "gbp"
      },
      "resultCash": {
        "expr": "round(cashOut)",
        "format": "gbp"
      },
      "resultDcIncome": {
        "expr": "round(dcIncomeOut)",
        "format": "gbp"
      },
      "resultTotal": {
        "expr": "round(pensionOut + dcIncomeOut + if(retireAgeEff >= statePensionAge, statePension, 0))",
        "format": "gbp"
      },
      "memberName": {
        "expr": "member.name"
      }
    },
    "series": {
      "incomeByAge": {
        "over": "age",
        "from": "retireAgeEff",
        "to": "90",
        "values": {
          "db": "round(pensionOut)",
          "dc": "if(age < retireAgeEff + 20, round(dcIncomeOut), 0)",
          "sp": "if(age >= statePensionAge, round(statePension), 0)"
        }
      }
    }
  },
  "queues": [
    {
      "actor": "member",
      "description": "Scheme member exploring retirement scenarios.",
      "displayName": "Member",
      "key": "web-user",
      "roleGates": [],
      "tags": {}
    },
    {
      "actor": "reviewer",
      "description": "Back-office queue handling formal quote requests.",
      "displayName": "Scheme administrators",
      "key": "business-user",
      "roleGates": [],
      "tags": {}
    }
  ],
  "stages": [
    {
      "actions": [],
      "actor": "member",
      "components": [
        {
          "type": "body",
          "content": "See what your benefits could be worth, explore your options for taking them, and model changes like retiring earlier. You can start from your current pension record, or from the figures on a retirement quote we've sent you."
        },
        {
          "type": "inset-text",
          "content": "Modelling uses your latest Annual Member Statement values and standard scheme assumptions, which you can change at any time."
        }
      ],
      "displayName": "Model your money",
      "queueKey": "web-user",
      "roleGates": [],
      "routes": [
        {
          "actions": [],
          "id": "choose-start--start-modelling--to-model-from-record",
          "label": "Model with my current record",
          "style": "primary",
          "target": "to-model-from-record",
          "trigger": "start-modelling"
        },
        {
          "actions": [],
          "id": "choose-start--use-quote--to-quote-entry",
          "label": "I have a retirement quote",
          "style": "secondary",
          "target": "to-quote-entry",
          "trigger": "use-quote"
        }
      ],
      "stageKey": "choose-start",
      "stageType": "Question"
    },
    {
      "actions": [],
      "actor": "member",
      "components": [
        {
          "type": "body",
          "content": "Copy the figures from the quote we sent you. You'll find them on the first page, under 'Your benefits'."
        },
        {
          "type": "fieldset",
          "children": [
            {
              "type": "decimal",
              "default": "0",
              "fieldKey": "qPension",
              "hint": "The yearly pension amount shown on your quote.",
              "label": "Yearly pension on your quote",
              "min": 0,
              "prefix": "£",
              "required": true
            },
            {
              "type": "decimal",
              "default": "0",
              "fieldKey": "qLump",
              "hint": "The one-off lump sum shown on your quote.",
              "label": "Lump sum on your quote",
              "min": 0,
              "prefix": "£",
              "required": true
            },
            {
              "type": "decimal",
              "default": "0",
              "fieldKey": "qDC",
              "hint": "Leave blank if your quote has no defined contribution savings.",
              "label": "DC pot value on your quote",
              "min": 0,
              "prefix": "£",
              "required": false
            },
            {
              "type": "number",
              "default": "66",
              "fieldKey": "qAge",
              "label": "Retirement age on your quote",
              "max": 75,
              "min": 55,
              "required": true
            }
          ],
          "legend": "Your quote figures",
          "legendSize": "m"
        }
      ],
      "displayName": "Enter your retirement quote",
      "queueKey": "web-user",
      "roleGates": [],
      "routes": [
        {
          "actions": [],
          "id": "enter-quote--use-quote-figures--to-model-from-quote",
          "label": "Use these figures",
          "style": "primary",
          "target": "to-model-from-quote",
          "trigger": "use-quote-figures"
        }
      ],
      "stageKey": "enter-quote",
      "stageType": "Question"
    },
    {
      "actions": [],
      "actor": "member",
      "components": [
        {
          "type": "body",
          "content": "Adjust your retirement age, how you take your benefits, and the assumptions behind the figures. All amounts are estimates before tax."
        },
        {
          "type": "inset-text",
          "content": "You're modelling with the figures from your retirement quote, so the retirement age and assumptions are fixed to match it.",
          "showWhen": "quoteMode"
        },
        {
          "type": "slider",
          "default": "66",
          "fieldKey": "retireAge",
          "hint": "Your Normal Pension Age is 66.",
          "label": "When do you want to retire?",
          "max": 75,
          "min": 55,
          "required": true,
          "showWhen": "not quoteMode",
          "step": 1
        },
        {
          "type": "warning-text",
          "content": "Retiring before 66 reduces your DB pension, because it's paid for longer.",
          "showWhen": "not quoteMode and retireAge < npa"
        },
        {
          "type": "radio",
          "default": "Standard benefits",
          "fieldKey": "benefitOption",
          "hint": "You can change your mind any time before you retire.",
          "label": "How do you want to take your benefits?",
          "options": [
            "Standard benefits",
            "Maximum tax-free cash",
            "Take DC pot as cash"
          ],
          "required": true
        },
        {
          "type": "heading",
          "content": "Assumptions",
          "level": 2,
          "showWhen": "not quoteMode"
        },
        {
          "type": "slider",
          "default": "2.5",
          "fieldKey": "inflation",
          "label": "Inflation (CPI)",
          "max": 5,
          "min": 0,
          "required": false,
          "showWhen": "not quoteMode",
          "step": 0.5,
          "suffix": "%"
        },
        {
          "type": "slider",
          "default": "3",
          "fieldKey": "salaryGrowth",
          "label": "Yearly salary growth",
          "max": 6,
          "min": 0,
          "required": false,
          "showWhen": "not quoteMode and member.active",
          "step": 0.5,
          "suffix": "%"
        },
        {
          "type": "slider",
          "default": "5",
          "fieldKey": "invReturn",
          "label": "Investment return",
          "max": 8,
          "min": 0,
          "required": false,
          "showWhen": "not quoteMode and hasDc",
          "step": 0.5,
          "suffix": "%"
        },
        {
          "type": "radio",
          "default": "Today's money",
          "fieldKey": "moneyBasis",
          "label": "Show amounts in",
          "options": [
            "Today's money",
            "Future money"
          ],
          "required": false,
          "showWhen": "not quoteMode"
        },
        {
          "type": "stat-group",
          "items": [
            {
              "emphasis": true,
              "fieldKey": "resultPension",
              "label": "DB pension",
              "qualifier": "a year, for life"
            },
            {
              "fieldKey": "resultCash",
              "label": "Cash",
              "qualifier": "one-off payment"
            },
            {
              "fieldKey": "resultDcIncome",
              "label": "DC income",
              "qualifier": "a year, over 20 years"
            },
            {
              "emphasis": true,
              "fieldKey": "resultTotal",
              "label": "Total income",
              "qualifier": "a year at your chosen age, incl. State Pension from 68"
            }
          ],
          "title": "Your estimated benefits"
        },
        {
          "type": "chart",
          "bands": [
            {
              "key": "db",
              "label": "DB pension"
            },
            {
              "key": "dc",
              "label": "DC drawdown"
            },
            {
              "key": "sp",
              "label": "State Pension"
            }
          ],
          "kind": "stacked-bar",
          "series": "incomeByAge",
          "title": "Your estimated yearly income by age",
          "x": "age",
          "xLabelEvery": 5
        },
        {
          "type": "inset-text",
          "content": "Figures are estimates for illustration only, based on the assumptions shown, and aren't a promise of what you'll get. Before making decisions, request a formal quote or consider taking financial advice."
        }
      ],
      "displayName": "Your money, modelled",
      "queueKey": "web-user",
      "roleGates": [],
      "routes": [
        {
          "actions": [],
          "id": "model--recalculate--recalculate-loop",
          "label": "Recalculate",
          "style": "secondary",
          "target": "recalculate-loop",
          "trigger": "recalculate"
        },
        {
          "actions": [],
          "id": "model--request-quote--fan-out-quote-request",
          "label": "Request a formal quote",
          "style": "primary",
          "target": "fan-out-quote-request",
          "trigger": "request-quote"
        }
      ],
      "stageKey": "model",
      "stageType": "Question"
    },
    {
      "actions": [],
      "actor": "member",
      "components": [
        {
          "type": "panel",
          "heading": "Your quote request has been sent"
        },
        {
          "type": "body",
          "content": "The scheme administrators will prepare a formal quote for your chosen scenario and send it to you. A formal quote gives you guaranteed figures you can rely on when deciding to retire."
        }
      ],
      "displayName": "Quote request sent",
      "queueKey": "web-user",
      "roleGates": [],
      "routes": [],
      "stageKey": "quote-requested",
      "stageType": "Confirmation"
    },
    {
      "actions": [],
      "actor": "reviewer",
      "components": [
        {
          "type": "body",
          "content": "The member has requested a formal quote for the scenario below. Confirm the figures against the administration system before issuing."
        },
        {
          "type": "summary-list",
          "children": [
            {
              "type": "text",
              "fieldKey": "memberName",
              "label": "Member"
            },
            {
              "type": "text",
              "fieldKey": "retireAge",
              "label": "Retirement age"
            },
            {
              "type": "text",
              "fieldKey": "benefitOption",
              "label": "Benefit option"
            },
            {
              "type": "text",
              "fieldKey": "resultPension",
              "label": "Estimated DB pension (a year)"
            },
            {
              "type": "text",
              "fieldKey": "resultCash",
              "label": "Estimated cash"
            },
            {
              "type": "text",
              "fieldKey": "resultTotal",
              "label": "Estimated total yearly income"
            }
          ],
          "title": "Requested scenario"
        }
      ],
      "description": "Back-office review of a member's modelled scenario before issuing a formal quote.",
      "displayName": "Review quote request",
      "queueKey": "business-user",
      "roleGates": [
        "reviewer"
      ],
      "routes": [
        {
          "actions": [],
          "id": "review-quote-request--send-quote--close-request",
          "label": "Issue formal quote",
          "style": "primary",
          "target": "close-request",
          "trigger": "send-quote"
        }
      ],
      "stageKey": "review-quote-request",
      "stageType": "Question"
    },
    {
      "actions": [],
      "actor": "reviewer",
      "components": [
        {
          "type": "panel",
          "heading": "Formal quote issued"
        },
        {
          "type": "body",
          "content": "The formal quote has been generated and sent to the member."
        }
      ],
      "displayName": "Formal quote issued",
      "queueKey": "business-user",
      "roleGates": [],
      "routes": [],
      "stageKey": "quote-sent",
      "stageType": "Confirmation"
    }
  ],
  "gateways": [
    {
      "displayName": "Start from record",
      "gatewayType": "Split",
      "key": "to-model-from-record",
      "queueKey": "web-user",
      "requiredIncomingQueues": [],
      "roleGates": [],
      "routes": [
        {
          "actions": [],
          "id": "to-model-from-record--continue--model",
          "target": "model",
          "trigger": "continue"
        }
      ]
    },
    {
      "displayName": "Start from quote",
      "gatewayType": "Split",
      "key": "to-quote-entry",
      "queueKey": "web-user",
      "requiredIncomingQueues": [],
      "roleGates": [],
      "routes": [
        {
          "actions": [],
          "id": "to-quote-entry--continue--enter-quote",
          "target": "enter-quote",
          "trigger": "continue"
        }
      ]
    },
    {
      "displayName": "Quote figures captured",
      "gatewayType": "Split",
      "key": "to-model-from-quote",
      "queueKey": "web-user",
      "requiredIncomingQueues": [],
      "roleGates": [],
      "routes": [
        {
          "actions": [],
          "id": "to-model-from-quote--continue--model",
          "target": "model",
          "trigger": "continue"
        }
      ]
    },
    {
      "displayName": "Recalculate",
      "gatewayType": "Split",
      "key": "recalculate-loop",
      "queueKey": "web-user",
      "requiredIncomingQueues": [],
      "roleGates": [],
      "routes": [
        {
          "actions": [],
          "id": "recalculate-loop--continue--model",
          "target": "model",
          "trigger": "continue"
        }
      ]
    },
    {
      "displayName": "Send quote request",
      "gatewayType": "Split",
      "key": "fan-out-quote-request",
      "queueKey": "web-user",
      "requiredIncomingQueues": [],
      "roleGates": [],
      "routes": [
        {
          "actions": [],
          "id": "fan-out-quote-request--continue--quote-requested",
          "target": "quote-requested",
          "trigger": "continue"
        },
        {
          "actions": [],
          "id": "fan-out-quote-request--continue--review-quote-request",
          "target": "review-quote-request",
          "trigger": "continue"
        }
      ]
    },
    {
      "displayName": "Close request",
      "gatewayType": "Split",
      "key": "close-request",
      "queueKey": "business-user",
      "requiredIncomingQueues": [],
      "roleGates": [],
      "routes": [
        {
          "actions": [],
          "id": "close-request--continue--quote-sent",
          "target": "quote-sent",
          "trigger": "continue"
        }
      ]
    }
  ]
});
