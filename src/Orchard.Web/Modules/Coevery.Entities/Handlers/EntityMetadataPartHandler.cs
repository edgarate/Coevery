﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Coevery.Core.Services;
using Coevery.Entities.Events;
using Coevery.Entities.Models;
using Orchard.ContentManagement;
using Orchard.ContentManagement.Handlers;
using Orchard.ContentManagement.MetaData;
using Orchard.ContentManagement.MetaData.Builders;
using Orchard.ContentManagement.MetaData.Services;
using Orchard.Data;
using Orchard.Logging;

namespace Coevery.Entities.Handlers {
    public class EntityMetadataPartHandler : ContentHandler {
        private readonly IRepository<FieldMetadataRecord> _fieldMetadataRepository;
        private readonly IContentManager _contentManager;
        private readonly IContentDefinitionManager _contentDefinitionManager;
        private readonly ISettingsFormatter _settingsFormatter;
        private readonly IEntityEvents _entityEvents;
        private readonly ISchemaUpdateService _schemaUpdateService;
        private readonly IFieldEvents _fieldEvents;

        public EntityMetadataPartHandler(
            IRepository<EntityMetadataRecord> entityMetadataRepository,
            IRepository<FieldMetadataRecord> fieldMetadataRepository,
            IContentManager contentManager,
            IContentDefinitionManager contentDefinitionManager,
            ISettingsFormatter settingsFormatter,
            IEntityEvents entityEvents,
            ISchemaUpdateService schemaUpdateService,
            IFieldEvents fieldEvents) {
            _fieldMetadataRepository = fieldMetadataRepository;
            _contentManager = contentManager;
            _contentDefinitionManager = contentDefinitionManager;
            _settingsFormatter = settingsFormatter;
            _entityEvents = entityEvents;
            _schemaUpdateService = schemaUpdateService;
            _fieldEvents = fieldEvents;

            Filters.Add(StorageFilter.For(entityMetadataRepository));
            OnVersioning<EntityMetadataPart>(OnVersioning);
            OnPublishing<EntityMetadataPart>(OnPublishing);
        }

        private void OnVersioning(VersionContentContext context, EntityMetadataPart existing, EntityMetadataPart building) {
            building.Record.FieldMetadataRecords = new List<FieldMetadataRecord>();
            foreach (var record in existing.Record.FieldMetadataRecords) {
                var newRecord = new FieldMetadataRecord();
                _fieldMetadataRepository.Copy(record, newRecord);
                newRecord.OriginalId = record.Id;
                _fieldMetadataRepository.Create(newRecord);
                building.Record.FieldMetadataRecords.Add(newRecord);
            }
        }

        private void OnPublishing(PublishContentContext context, EntityMetadataPart part) {
            if (context.PreviousItemVersionRecord == null) {
                CreateEntity(part);
            }
            else {
                var previousEntity = _contentManager.Get<EntityMetadataPart>(context.Id);
                UpdateEntity(previousEntity, part);
            }
        }

        private void CreateEntity(EntityMetadataPart part) {
            _contentDefinitionManager.AlterPartDefinition(part.Name, builder => {
                foreach (var fieldMetadataRecord in part.FieldMetadataRecords) {
                    AddField(builder, fieldMetadataRecord);
                }
            });

            _contentDefinitionManager.AlterTypeDefinition(part.Name, builder => {
                builder.DisplayedAs(part.DisplayName);
                builder.WithPart(part.Name).WithPart("CoeveryCommonPart");
            });

            _entityEvents.OnCreated(part.Name);

            _schemaUpdateService.CreateTable(part.Name, context => {
                foreach (var fieldMetadataRecord in part.FieldMetadataRecords) {
                    context.FieldColumn(fieldMetadataRecord.Name,
                        fieldMetadataRecord.ContentFieldDefinitionRecord.Name);
                }
            });
        }

        private void UpdateEntity(EntityMetadataPart previousEntity, EntityMetadataPart entity) {
            _contentDefinitionManager.AlterTypeDefinition(entity.Name, builder =>
                builder.DisplayedAs(entity.DisplayName));

            foreach (var fieldMetadataRecord in previousEntity.FieldMetadataRecords) {
                bool exist = entity.FieldMetadataRecords.Any(x => x.OriginalId == fieldMetadataRecord.Id);
                if (!exist) {
                    _fieldEvents.OnDeleting(entity.Name, fieldMetadataRecord.Name);
                    var record = fieldMetadataRecord;
                    _contentDefinitionManager.AlterPartDefinition(entity.Name,
                        typeBuilder => typeBuilder.RemoveField(record.Name));
                    _schemaUpdateService.DropColumn(entity.Name, fieldMetadataRecord.Name);
                }
            }

            var partDefinition = _contentDefinitionManager.GetPartDefinition(entity.Name);
            var fields = partDefinition.Fields.ToList();
            foreach (var fieldMetadataRecord in entity.FieldMetadataRecords) {
                if (fieldMetadataRecord.OriginalId != 0) {
                    var settings = _settingsFormatter.Map(Parse(fieldMetadataRecord.Settings));
                    var field = fields.First(x => x.Name == fieldMetadataRecord.Name);
                    field.Settings.Clear();
                    foreach (var setting in settings) {
                        field.Settings.Add(setting.Key, setting.Value);
                    }
                }
                else {
                    var record = fieldMetadataRecord;
                    _contentDefinitionManager.AlterPartDefinition(entity.Name, builder => AddField(builder, record));
                    _schemaUpdateService.CreateColumn(entity.Name, record.Name, record.ContentFieldDefinitionRecord.Name);
                }
            }

            _contentDefinitionManager.StorePartDefinition(partDefinition);
        }

        private void AddField(ContentPartDefinitionBuilder partBuilder, FieldMetadataRecord record) {
            var settings = _settingsFormatter.Map(Parse(record.Settings));
            string fieldTypeName = record.ContentFieldDefinitionRecord.Name;

            partBuilder.WithField(record.Name, fieldBuilder => {
                fieldBuilder.OfType(fieldTypeName);
                foreach (var setting in settings) {
                    fieldBuilder.WithSetting(setting.Key, setting.Value);
                }
            });
        }

        private XElement Parse(string settings) {
            if (string.IsNullOrEmpty(settings)) {
                return null;
            }

            try {
                return XElement.Parse(settings);
            }
            catch (Exception ex) {
                Logger.Error(ex, "Unable to parse settings xml");
                return null;
            }
        }
    }
}