"""Pin audit decisions that must not silently claim runtime support."""
import unittest
from unittest.mock import patch

import audit


class AuditTests(unittest.TestCase):
    def test_cn406_is_part_of_default_legacy_scope(self):
        self.assertIn("cn-406", audit.BUILDS)

    def test_shared_scalar_reader_keeps_its_structure_dependency(self):
        code = 'PacketCodec.ReadScalars(tables, build, "InventoryUseRequest", payload);'
        found = audit.scan_names(code)[2]
        self.assertEqual({'InventoryUseRequest'}, found)

    def test_dynamic_structure_does_not_capture_a_field_key_or_next_call(self):
        code = ('PacketCodec.ReadScalars(tables, build, structure, body)["actor"]; '
                'PacketCodec.Write(tables, build, "RoundCountdown", values);')
        self.assertEqual({'RoundCountdown'}, audit.scan_names(code)[2])

    def test_nested_arguments_and_quoted_commas_do_not_shift_the_structure(self):
        code = ('PacketCodec.ReadScalars(Resolve("quoted,)value", context), '
                'BuildOf(profile, other), "RoundDeathReport", body);')
        self.assertEqual({'RoundDeathReport'}, audit.scan_names(code)[2])

    def test_named_literal_structure_argument_is_recognized(self):
        code = 'PacketCodec.Write(tables: tables, build: build, name: "RoundStartGame", values: fields);'
        self.assertEqual({'RoundStartGame'}, audit.scan_names(code)[2])

    def test_incomplete_call_cannot_borrow_a_later_literal(self):
        code = 'PacketCodec.ReadScalars(tables, build); var unknown = "actor";'
        self.assertEqual(set(), audit.scan_names(code)[2])

    def setUp(self):
        self.feature = dict(plugin="room", option="Example", default=False, mapped=True,
                            req={('c2s', 'gcpExample')}, opt=set(), structs={'Example'}, values=[])
        self.build = dict(config={'room': {'example': False}}, opcodes={('c2s', 'gcpExample'): 'verified'},
                          structs={'Example': 1}, values={}, lineage=['kr-812'])

    def test_explicit_false_overrides_default_true(self):
        self.feature['default'] = True
        self.assertEqual('off', audit.evaluate('example', self.feature, self.build)[0])

    def test_string_false_cannot_become_true(self):
        self.build['config']['room']['example'] = 'false'
        with self.assertRaises(ValueError):
            audit.evaluate('example', self.feature, self.build)

    def test_enabled_static_dependencies_are_not_client_validation(self):
        self.build['config']['room']['example'] = True
        self.assertEqual('static-ready', audit.evaluate('example', self.feature, self.build)[0])

    def test_unmapped_feature_is_not_ready(self):
        self.feature['mapped'] = False
        self.assertEqual('unmapped', audit.evaluate('example', self.feature, self.build)[0])

    def test_native_empty_body_is_a_valid_structure(self):
        self.feature['structs'] = {'PveRevivalRequest'}
        self.build['structs'] = {'PveRevivalRequest': 0}
        self.assertEqual('off', audit.evaluate('example', self.feature, self.build)[0])

    def test_missing_empty_body_structure_is_still_missing(self):
        self.feature['structs'] = {'PveRevivalRequest'}
        self.build['structs'] = {}
        self.assertEqual('struct-missing:PveRevivalRequest', audit.evaluate('example', self.feature, self.build)[0])

    def test_composition_failure_does_not_use_a_partial_profile(self):
        with patch.object(audit, 'dump_config', side_effect=RuntimeError('composition failed')):
            with self.assertRaisesRegex(RuntimeError, 'Composed configuration'):
                audit.build_data('kr-812', {}, {})

    def test_unmounted_preset_family_does_not_block_equipment(self):
        self.feature['structs'] = {'Example', 'WeaponPresetUpdate'}
        cell, details = audit.evaluate('example', self.feature, self.build)
        self.assertEqual('off', cell)
        self.assertEqual(['WeaponPresetUpdate'], details['inactive_structs'])

    def test_capability_gate_follows_the_packet_tables(self):
        self.build['config']['room']['example'] = True
        with patch.dict(audit.CAPABILITY_GATES, {'example': 'example_capability'}):
            self.assertEqual('gated:capability', audit.evaluate('example', self.feature, self.build)[0])
            self.build['values'] = {'capabilities': {'example_capability': 'true'}}
            self.assertEqual('static-ready', audit.evaluate('example', self.feature, self.build)[0])

    def test_named_preset_sender_requires_its_layout(self):
        self.feature['structs'] = {'Example', 'WeaponPresetUpdate'}
        self.build['opcodes'][('s2c', 'gspReqWeaponPreSetUpdate')] = 'verified'
        self.assertEqual('struct-missing:WeaponPresetUpdate',
                         audit.evaluate('example', self.feature, self.build)[0])


if __name__ == '__main__':
    unittest.main()
